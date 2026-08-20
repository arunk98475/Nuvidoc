using Docovee.BLL;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.BLL.Services.PatientPush;
using Docovee.Hubs;
using Docovee.Pages.Account;
using Docovee.Services.Push;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Xml.Linq;

var contentRoot = Directory.GetCurrentDirectory();
var webRoot = Path.Combine(contentRoot, "wwwroot");
TryCreateDirectory(webRoot);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = webRoot
});

var uploadsPath = Path.Combine(webRoot, "uploads", "doctors");
var patientUploadsPath = Path.Combine(webRoot, "uploads", "patients");
var contentUploadsPath = Path.Combine(webRoot, "uploads", "content");
var legalUploadsPath = Path.Combine(webRoot, "uploads", "legal");
var maxUploadBytes = ReadMaxAllowedContentLength(contentRoot);
// Do not fail startup if IIS app-pool identity cannot create folders —
// grant Modify on wwwroot\uploads (see deploy notes) and folders are created on first upload.
TryCreateDirectory(uploadsPath);
TryCreateDirectory(patientUploadsPath);
TryCreateDirectory(contentUploadsPath);
TryCreateDirectory(legalUploadsPath);
builder.Services.Configure<UploadOptions>(options =>
{
    options.DoctorsPhysicalPath = uploadsPath;
    options.DoctorsPublicPath = "/uploads/doctors";
    options.PatientsPhysicalPath = patientUploadsPath;
    options.PatientsPublicPath = "/uploads/patients";
    options.ContentImagesPhysicalPath = contentUploadsPath;
    options.ContentImagesPublicPath = "/uploads/content";
    options.LegalPdfsPhysicalPath = legalUploadsPath;
    options.LegalPdfsPublicPath = "/uploads/legal";
    options.MaxUploadBytes = maxUploadBytes;
});

static long ReadMaxAllowedContentLength(string contentRoot)
{
    var path = Path.Combine(contentRoot, "web.config");
    if (!File.Exists(path))
        return UploadOptions.DefaultMaxUploadBytes;

    try
    {
        var doc = System.Xml.Linq.XDocument.Load(path);
        var attr = doc.Descendants("requestLimits")
            .Attributes("maxAllowedContentLength")
            .FirstOrDefault();
        if (attr != null && long.TryParse(attr.Value, out var bytes) && bytes > 0)
            return bytes;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[NuviDoc] WARNING: could not read maxAllowedContentLength from web.config — {ex.Message}");
    }

    return UploadOptions.DefaultMaxUploadBytes;
}

static void TryCreateDirectory(string path)
{
    try
    {
        Directory.CreateDirectory(path);
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.WriteLine($"[NuviDoc] WARNING: cannot create '{path}' — {ex.Message}. Grant Modify to the IIS App Pool identity on wwwroot\\uploads.");
    }
    catch (IOException ex)
    {
        Console.WriteLine($"[NuviDoc] WARNING: cannot create '{path}' — {ex.Message}");
    }
}

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthRoles.Admin, policy => policy.RequireRole(AuthRoles.Admin));
    options.AddPolicy(AuthRoles.Patient, policy => policy.RequireRole(AuthRoles.Patient));
    options.AddPolicy(AuthRoles.Doctor, policy => policy.RequireRole(AuthRoles.Doctor));
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", AuthRoles.Admin);
    options.Conventions.AllowAnonymousToPage("/Admin/Login");
    options.Conventions.AllowAnonymousToPage("/Admin/Logout");

    options.Conventions.AuthorizeFolder("/Account");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/ExternalLogin");
    options.Conventions.AllowAnonymousToPage("/Account/Register");
    options.Conventions.AllowAnonymousToPage("/Account/Register/Doctor");
    options.Conventions.AllowAnonymousToPage("/Account/Logout");
    options.Conventions.AllowAnonymousToPage("/Account/Admin/Index");
    options.Conventions.AuthorizePage("/Account/Profile", AuthRoles.Patient);
    options.Conventions.AuthorizePage("/Account/Profile/Edit", AuthRoles.Patient);
    options.Conventions.AuthorizePage("/Account/DoctorProfile", AuthRoles.Doctor);
    options.Conventions.AuthorizePage("/Account/DoctorProfile/Edit", AuthRoles.Doctor);
    options.Conventions.AuthorizeFolder("/Doctor", AuthRoles.Doctor);
    options.Conventions.AllowAnonymousToFolder("/Doctors");
});
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    // Native MAUI apps (emulator / device) call the API outside the web origin.
    options.AddPolicy("MobileApp", policy =>
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddDocoveeBll(builder.Configuration);
builder.Services.AddScoped<IPatientPushChannel, SignalRPatientPushChannel>();
builder.Services.AddHostedService<Docovee.Services.DatabaseStartupHostedService>();
builder.Services.AddHostedService<Docovee.Services.PmsInboundSyncHostedService>();
builder.Services.AddHostedService<Docovee.Services.VoiceCallRetryHostedService>();
builder.Services.AddHostedService<Docovee.Services.AppointmentReminderHostedService>();
builder.Services.AddHostedService<Docovee.Services.DoctorQualityScoreHostedService>();

const string smartScheme = "CookieOrBearer";
var mobileJwt = builder.Configuration.GetSection(MobileJwtOptions.SectionName).Get<MobileJwtOptions>()
    ?? new MobileJwtOptions();
var signingKey = string.IsNullOrWhiteSpace(mobileJwt.SigningKey) || mobileJwt.SigningKey.Length < 32
    ? "CHANGE-ME-NUVIDOC-MOBILE-JWT-SIGNING-KEY-32+CHARS-MIN"
    : mobileJwt.SigningKey;

var authBuilder = builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = smartScheme;
        options.DefaultChallengeScheme = smartScheme;
        options.DefaultScheme = smartScheme;
    })
    .AddPolicyScheme(smartScheme, "Cookie or JWT Bearer", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var header = context.Request.Headers.Authorization.FirstOrDefault();
            if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return JwtBearerDefaults.AuthenticationScheme;

            var accessToken = context.Request.Query["access_token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(accessToken)
                && context.Request.Path.StartsWithSegments("/hubs"))
                return JwtBearerDefaults.AuthenticationScheme;

            return CookieAuthenticationDefaults.AuthenticationScheme;
        };
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    })
    .AddCookie(ExternalLoginModel.ExternalScheme, options =>
    {
        options.Cookie.Name = ".NuviDoc.External";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = mobileJwt.Issuer,
            ValidAudience = mobileJwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.FromMinutes(2)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authBuilder.AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        options.SignInScheme = ExternalLoginModel.ExternalScheme;
        options.CallbackPath = "/signin-google";
        options.SaveTokens = false;
    });
}

builder.Services.AddHttpsRedirection(options =>
{
    // Only force HTTPS in Production. In Development / LAN (e.g. :37788) this
    // redirect was sending browsers to https://host:443 → appears as host with no port.
    if (!builder.Environment.IsDevelopment())
    {
        options.HttpsPort = 443;
        options.RedirectStatusCode = StatusCodes.Status307TemporaryRedirect;
    }
});

var app = builder.Build();

Console.WriteLine("[NuviDoc] Web server starting — open http://localhost:5274, https://localhost:7212, or LAN profile http://192.168.1.13:37788");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseCors("MobileApp");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();
app.MapHub<PatientNotificationsHub>("/hubs/patient-notifications");

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("[NuviDoc] ✓ Server is listening — browse to http://localhost:5274, https://localhost:7212, or LAN http://192.168.1.13:37788");
});

app.Run();
