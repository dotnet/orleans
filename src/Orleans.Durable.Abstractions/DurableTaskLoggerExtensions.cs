using Microsoft.Extensions.Logging;

namespace Orleans.DurableTasks;

/// <summary>Provides logging which reports each event of a durable task once.</summary>
public static class DurableTaskLoggerExtensions
{
    /// <summary>
    /// Returns a logger which writes to <paramref name="logger"/> except while the calling code replays a durable task's
    /// recorded operations.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <returns>A logger which skips events while <see cref="DurableTaskContext.IsReplaying"/> is <see langword="true"/>.</returns>
    /// <remarks>
    /// The logger reads the running task when each event is logged, so a grain caches one and uses it from durable task
    /// bodies and ordinary methods alike; outside a durable task, it writes every event. While a task replays, it reports
    /// that it isn't enabled, so source-generated logging methods skip formatting their messages. Scopes always begin on
    /// <paramref name="logger"/>.
    /// </remarks>
    public static ILogger ReplaySafe(this ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return logger as ReplaySafeLogger ?? new ReplaySafeLogger(logger);
    }

    private sealed class ReplaySafeLogger(ILogger logger) : ILogger
    {
        private static bool IsReplaying => DurableTaskContext.Ambient is { IsReplaying: true };

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => !IsReplaying && logger.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsReplaying)
            {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
