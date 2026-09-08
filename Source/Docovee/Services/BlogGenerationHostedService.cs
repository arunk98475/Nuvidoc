using Docovee.BLL.Services;

namespace Docovee.Services;

/// <summary>Processes the blog topic queue every hour when the interval is due.</summary>
public sealed class BlogGenerationHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BlogGenerationHostedService> _logger;

    public BlogGenerationHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<BlogGenerationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Blog generation worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var blogGen = scope.ServiceProvider.GetRequiredService<IBlogGenerationService>();
                var result = await blogGen.ProcessDueGenerationAsync(force: false, stoppingToken);
                if (result.Success)
                {
                    _logger.LogInformation(
                        "Generated blog draft for topic {TopicId}: {Title}",
                        result.Topic?.Id,
                        result.Page?.Title);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Blog generation worker cycle failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Blog generation worker stopped.");
    }
}
