using Docovee.Integrations.Configuration;
using Docovee.Integrations.Contracts;
using Docovee.Integrations.NexHealth;
using Docovee.Integrations.OpenDental;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Docovee.Integrations;

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddDocoveeIntegrations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<OpenDentalOptions>(configuration.GetSection(OpenDentalOptions.SectionName));
        services.Configure<NexHealthOptions>(configuration.GetSection(NexHealthOptions.SectionName));

        services.AddHttpClient("OpenDental", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddHttpClient("NexHealth", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddSingleton<OpenDentalProvider>();
        services.AddSingleton<NexHealthProvider>();
        services.AddSingleton<IEnumerable<IPmsProvider>>(sp =>
        [
            sp.GetRequiredService<OpenDentalProvider>(),
            sp.GetRequiredService<NexHealthProvider>()
        ]);

        return services;
    }
}
