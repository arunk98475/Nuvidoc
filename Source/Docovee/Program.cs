using Docovee.BLL;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.BLL.Data;
using Docovee.BLL.Services;
using Docovee.DS;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

var uploadsPath = Path.Combine(builder.Environment.WebRootPath, "uploads", "doctors");
Directory.CreateDirectory(uploadsPath);
builder.Services.Configure<UploadOptions>(options =>
{
    options.DoctorsPhysicalPath = uploadsPath;
    options.DoctorsPublicPath = "/uploads/doctors";
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
    options.Conventions.AllowAnonymousToPage("/Account/Register");
    options.Conventions.AllowAnonymousToPage("/Account/Register/Doctor");
    options.Conventions.AllowAnonymousToPage("/Account/Logout");
    options.Conventions.AllowAnonymousToPage("/Account/Admin/Index");
    options.Conventions.AuthorizePage("/Account/Profile", AuthRoles.Patient);
    options.Conventions.AuthorizePage("/Account/Profile/Edit", AuthRoles.Patient);
    options.Conventions.AuthorizePage("/Account/DoctorProfile", AuthRoles.Doctor);
    options.Conventions.AuthorizePage("/Account/DoctorProfile/Edit", AuthRoles.Doctor);
});
builder.Services.AddControllers();
builder.Services.AddDocoveeBll(builder.Configuration);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });

builder.Services.AddHttpsRedirection(options =>
{
    options.HttpsPort = 443;
    options.RedirectStatusCode = StatusCodes.Status307TemporaryRedirect;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<DocoveeDbContext>();
        var adminOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminOptions>>().Value;
        await SchemaUpdater.EnsureLatestSchemaAsync(db);
        await SeedData.InitializeAsync(db);
        await PollingQuestionSync.SyncFromSpecAsync(db);
        await SeedData.InitializeAdminAndSettingsAsync(db, adminOptions);
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Database initialization failed. Ensure MySQL is running and the connection string is correct.");
    }
}

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

app.Run();
