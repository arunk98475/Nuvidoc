using Docovee.BLL.Audit;
using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.Integrations;
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
                "Set it in appsettings.Production.json on the server before starting the app.");

        if (!connectionString.Contains("Connect Timeout", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("Connection Timeout", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";Connect Timeout=15";
        }

        services.AddHttpContextAccessor();
        services.AddScoped<IAuditTrailService, AuditTrailService>();
        services.AddScoped<AuditSaveChangesInterceptor>();

        services.AddDbContext<DocoveeDbContext>((sp, options) =>
        {
            options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36)), mySqlOptions =>
            {
                mySqlOptions.CommandTimeout(60);
                mySqlOptions.EnableRetryOnFailure(2, TimeSpan.FromSeconds(2), null);
            });
            options.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
        });

        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
        services.Configure<AdminOptions>(configuration.GetSection(AdminOptions.SectionName));
        services.Configure<SiteOptions>(configuration.GetSection(SiteOptions.SectionName));
        services.Configure<ChatBotOptions>(configuration.GetSection(ChatBotOptions.SectionName));
        services.Configure<TwilioOptions>(configuration.GetSection(TwilioOptions.SectionName));
        services.Configure<ElevenLabsOptions>(configuration.GetSection(ElevenLabsOptions.SectionName));
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
        services.AddHttpClient<IWebDoctorDiscoveryService, WebDoctorDiscoveryService>();
        services.AddHttpClient<IClaudeGoogleReviewService, ClaudeGoogleReviewService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });
        services.AddHttpClient<INuviVoiceCallingService, ElevenLabsTwilioCallingService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddScoped<IDoctorSearchService, DoctorSearchService>();
        services.AddScoped<IPublicDoctorService, PublicDoctorService>();
        services.AddScoped<IInsuranceService, InsuranceService>();
        services.AddScoped<IDoctorInsuranceService, DoctorInsuranceService>();
        services.AddScoped<IPatientService, PatientService>();
        services.AddScoped<IPatientFileService, PatientFileService>();
        services.AddScoped<IPatientInsuranceProfileService, PatientInsuranceProfileService>();
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
        services.AddScoped<IDoctorMediaService, DoctorMediaService>();
        services.AddScoped<IPatientDoctorContactService, PatientDoctorContactService>();
        services.AddScoped<IAppointmentService, AppointmentService>();
        services.AddScoped<IDoctorLocationService, DoctorLocationService>();
        services.AddSingleton<IDoctorImportJobService, DoctorImportJobService>();
        services.AddScoped<IAppSettingsService, AppSettingsService>();
        services.AddScoped<IContentPageService, ContentPageService>();
        services.AddScoped<IHomePageContentService, HomePageContentService>();
        services.AddDocoveeIntegrations(configuration);
        services.AddScoped<IPmsCalendarService, PmsCalendarService>();

        return services;
    }
}
