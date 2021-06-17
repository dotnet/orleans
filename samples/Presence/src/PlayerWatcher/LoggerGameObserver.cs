using Microsoft.Extensions.Logging;
using Presence.Grains;

namespace Presence.PlayerWatcher
{
    /// <summary>
    /// Implements a <see cref="IGameObserver"/> that outputs notifications to the given logger.
    /// </summary>
    public class LoggerGameObserver : IGameObserver
    {
        private readonly ILogger<LoggerGameObserver> logger;

        public LoggerGameObserver(ILogger<LoggerGameObserver> logger)
        {
            this.logger = logger;
        }

        public void UpdateGameScore(string score)
        {
            logger.LogInformation("New game score: {GameScore}", score);
        }
    }
}
