using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Docovee.logging;

public static class DocoveeLoggingExtensions
{
    public static IServiceCollection AddDocoveeLogging(this IServiceCollection services)
    {
        services.AddSingleton<IDocoveeLogger>(sp =>
            new DocoveeLogger(sp.GetRequiredService<ILoggerFactory>(), "Docovee"));
        return services;
    }

    public static IDocoveeLogger CreateLogger(this IServiceProvider services, string categoryName) =>
        new DocoveeLogger(services.GetRequiredService<ILoggerFactory>(), categoryName);
}
