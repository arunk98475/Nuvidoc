using Docovee.BLL;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.Pages.Account;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http;

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
// Do not fail startup if IIS app-pool identity cannot create folders —
// grant Modify on wwwroot\uploads (see deploy notes) and folders are created on first upload.
TryCreateDirectory(uploadsPath);
TryCreateDirectory(patientUploadsPath);
TryCreateDirectory(contentUploadsPath);
builder.Services.Configure<UploadOptions>(options =>
{
    options.DoctorsPhysicalPath = uploadsPath;
    options.DoctorsPublicPath = "/uploads/doctors";
    options.PatientsPhysicalPath = patientUploadsPath;
    options.PatientsPublicPath = "/uploads/patients";
    options.ContentImagesPhysicalPath = contentUploadsPath;
    options.ContentImagesPublicPath = "/uploads/content";
});

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
builder.Services.AddDocoveeBll(builder.Configuration);
builder.Services.AddHostedService<Docovee.Services.DatabaseStartupHostedService>();
builder.Services.AddHostedService<Docovee.Services.PmsInboundSyncHostedService>();

var authBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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
    options.HttpsPort = 443;
    options.RedirectStatusCode = StatusCodes.Status307TemporaryRedirect;
});

var app = builder.Build();

Console.WriteLine("[NuviDoc] Web server starting — open https://localhost:7212 or http://localhost:5274");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("[NuviDoc] ✓ Server is listening — browse to https://localhost:7212 or http://localhost:5274");
});

app.Run();
