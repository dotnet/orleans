using Microsoft.Extensions.Logging;
using Presence.Grains;

namespace Presence.PlayerWatcher;

/// <summary>
/// Implements a <see cref="IGameObserver"/> that outputs notifications to the given logger.
/// </summary>
public class LoggerGameObserver : IGameObserver
{
    private readonly ILogger<LoggerGameObserver> _logger;

    public LoggerGameObserver(ILogger<LoggerGameObserver> logger)
    {
        _logger = logger;
    }

    public void UpdateGameScore(string score) =>
        _logger.LogInformation("New game score: {GameScore}", score);
}
