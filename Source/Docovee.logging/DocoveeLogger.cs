using Microsoft.Extensions.Logging;

namespace Docovee.logging;

public class DocoveeLogger : IDocoveeLogger
{
    private readonly ILogger _logger;

    public DocoveeLogger(ILoggerFactory loggerFactory, string categoryName)
    {
        _logger = loggerFactory.CreateLogger(categoryName);
    }

    public void LogInformation(string message, params object[] args) =>
        _logger.LogInformation(message, args);

    public void LogWarning(string message, params object[] args) =>
        _logger.LogWarning(message, args);

    public void LogError(Exception exception, string message, params object[] args) =>
        _logger.LogError(exception, message, args);

    public void LogDebug(string message, params object[] args) =>
        _logger.LogDebug(message, args);
}
