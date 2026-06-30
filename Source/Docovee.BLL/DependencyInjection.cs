using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace Docovee.BLL;

public static class DependencyInjection
{
    public static IServiceCollection AddDocoveeBll(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Connection string 'DefaultConnection' is missing or empty. " +
                "Set it in appsettings.json or appsettings.Production.json on the server before starting the app.");

        services.AddDbContext<DocoveeDbContext>(options =>
            options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36))));

        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
        services.Configure<AdminOptions>(configuration.GetSection(AdminOptions.SectionName));
        services.Configure<SiteOptions>(configuration.GetSection(SiteOptions.SectionName));
        services.Configure<ChatBotOptions>(configuration.GetSection(ChatBotOptions.SectionName));
        services.AddSingleton<IBrandingService, BrandingService>();

        services.AddDocoveeLogging();
        services.AddHttpClient("DoctorPhotoDownload", (sp, client) =>
        {
            var siteName = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SiteOptions>>().Value.Name;
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", $"{siteName}/1.0");
        });
        services.AddHttpClient<IAnthropicChatService, AnthropicChatService>();
        services.AddHttpClient<IAnthropicValidationService, AnthropicValidationService>();
        services.AddHttpClient<IAnthropicMatchingService, AnthropicMatchingService>();

        services.AddScoped<IDoctorSearchService, DoctorSearchService>();
        services.AddScoped<IInsuranceService, InsuranceService>();
        services.AddScoped<IPatientService, PatientService>();
        services.AddScoped<IAdminAuthService, AdminAuthService>();
        services.AddScoped<IAccountAuthService, AccountAuthService>();
        services.AddScoped<IAccountRegistrationService, AccountRegistrationService>();
        services.AddScoped<IDoctorOnboardingService, DoctorOnboardingService>();
        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<IAdminPatientService, AdminPatientService>();
        services.AddScoped<IAdminDoctorService, AdminDoctorService>();
        services.AddScoped<IDoctorFileService, DoctorFileService>();
        services.AddScoped<IPollingQuestionService, PollingQuestionService>();
        services.AddScoped<IDoctorLanguageService, DoctorLanguageService>();
        services.AddScoped<IDoctorReviewService, DoctorReviewService>();
        services.AddSingleton<IDoctorImportJobService, DoctorImportJobService>();
        services.AddScoped<IAppSettingsService, AppSettingsService>();

        return services;
    }
}
