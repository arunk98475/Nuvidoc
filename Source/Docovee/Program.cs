using Docovee.BLL;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.BLL.Security;
using Docovee.BLL.Services;
using Docovee.BLL.Services.PatientPush;
using Docovee.Hubs;
using Docovee.Pages.Account;
using Docovee.Services.Push;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
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

ProductionSecretsGuard.Validate(builder.Environment, builder.Configuration);

var uploadsPath = Path.Combine(webRoot, "uploads", "doctors");
var contentUploadsPath = Path.Combine(webRoot, "uploads", "content");
var legalUploadsPath = Path.Combine(webRoot, "uploads", "legal");
var maxUploadBytes = ReadMaxAllowedContentLength(contentRoot);
// Do not fail startup if IIS app-pool identity cannot create folders —
// grant Modify on wwwroot\uploads (see deploy notes) and folders are created on first upload.
TryCreateDirectory(uploadsPath);
TryCreateDirectory(contentUploadsPath);
TryCreateDirectory(legalUploadsPath);
builder.Services.Configure<UploadOptions>(options =>
{
    options.DoctorsPhysicalPath = uploadsPath;
    options.DoctorsPublicPath = "/uploads/doctors";
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

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // IIS / reverse proxy terminates TLS; clear defaults so forwarded headers are honored.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("chat", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("phoneVerify", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("bookings", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
builder.Services.AddDocoveeBll(builder.Configuration);
builder.Services.AddScoped<IPatientPushChannel, SignalRPatientPushChannel>();
builder.Services.AddHostedService<Docovee.Services.DatabaseStartupHostedService>();
builder.Services.AddHostedService<Docovee.Services.PmsInboundSyncHostedService>();
builder.Services.AddHostedService<Docovee.Services.VoiceCallRetryHostedService>();
builder.Services.AddHostedService<Docovee.Services.AppointmentReminderHostedService>();
builder.Services.AddHostedService<Docovee.Services.PatientNurtureHostedService>();
builder.Services.AddHostedService<Docovee.Services.PatientAccountLifecycleHostedService>();
builder.Services.AddHostedService<Docovee.Services.DoctorQualityScoreHostedService>();
builder.Services.AddHostedService<Docovee.Services.SponsorshipBillingHostedService>();

var isDevelopment = builder.Environment.IsDevelopment();
var authBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        // Sliding idle timeout for patient/doctor sessions (admin uses shorter absolute ticket).
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = true;
        options.Cookie.Name = ".NuviDoc.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = isDevelopment
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    })
    .AddCookie(ExternalLoginModel.ExternalScheme, options =>
    {
        options.Cookie.Name = ".NuviDoc.External";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = isDevelopment
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
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

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
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
