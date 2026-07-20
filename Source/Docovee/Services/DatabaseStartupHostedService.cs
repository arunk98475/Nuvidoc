using Docovee.BLL.Audit;
using Docovee.BLL.Configuration;
using Docovee.BLL.Data;
using Docovee.DS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docovee.Services;

/// <summary>
/// Runs schema updates and seed data in the background so Kestrel can start immediately.
/// </summary>
public sealed class DatabaseStartupHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseStartupHostedService> _logger;

    public DatabaseStartupHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseStartupHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = InitializeInBackgroundAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task InitializeInBackgroundAsync()
    {
        try
        {
            _logger.LogInformation("Database startup: applying schema and seed (background)…");
            Console.WriteLine("[NuviDoc] Database startup running in background…");

            using var scope = _scopeFactory.CreateScope();
            Console.WriteLine("[NuviDoc DB] Resolving DbContext…");
            var db = scope.ServiceProvider.GetRequiredService<DocoveeDbContext>();
            Console.WriteLine("[NuviDoc DB] DbContext ready.");
            var adminOptions = scope.ServiceProvider.GetRequiredService<IOptions<AdminOptions>>().Value;

            using (AuditTrailScope.Suppress())
            {
                LogStep("Schema update…");
                await SchemaUpdater.EnsureLatestSchemaAsync(db);
                LogStep("Seed data…");
                await SeedData.InitializeAsync(db);
                LogStep("Polling questions…");
                await PollingQuestionSync.SyncFromSpecAsync(db);
                LogStep("Admin & settings…");
                await SeedData.InitializeAdminAndSettingsAsync(db, adminOptions);
            }

            _logger.LogInformation("Database startup completed.");
            Console.WriteLine("[NuviDoc] Database startup completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database startup failed. Ensure MySQL is running and DefaultConnection is correct.");
            Console.WriteLine("[NuviDoc] Database startup FAILED — check MySQL and ConnectionStrings:DefaultConnection.");
            Console.WriteLine($"[NuviDoc] {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"[NuviDoc] Inner: {ex.InnerException.Message}");
        }
    }

    private static void LogStep(string step) => Console.WriteLine($"[NuviDoc DB] {step}");
}
