#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Orleans.TestingHost.Logging;

/// <summary>
/// The in-memory log buffer which all <see cref="InMemoryLogger"/> instances share.
/// Useful for simulation testing where logs need to be captured and inspected.
/// </summary>
/// <remarks>
/// Creates a new in-memory log buffer.
/// </remarks>
/// <param name="timeProvider">Optional time provider for timestamps. Defaults to system time.</param>
public sealed class InMemoryLogBuffer(TimeProvider? timeProvider = null)
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Gets all log entries currently buffered.
    /// </summary>
    public IReadOnlyList<LogEntry> AllEntries => [.. _entries];

    /// <summary>
    /// Gets log entries filtered by minimum log level.
    /// </summary>
    public IEnumerable<LogEntry> GetEntries(LogLevel minimumLevel) => _entries.Where(e => e.LogLevel >= minimumLevel);

    /// <summary>
    /// Returns true if any entries exist at or above the specified log level.
    /// </summary>
    public bool HasEntriesAtOrAbove(LogLevel level) => _entries.Any(e => e.LogLevel >= level);

    /// <summary>
    /// Returns true if any warning or error entries exist.
    /// </summary>
    public bool HasWarningsOrErrors => HasEntriesAtOrAbove(LogLevel.Warning);

    /// <summary>
    /// Logs a message to the buffer.
    /// </summary>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter,
        string category)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var message = formatter(state, exception);
        var entry = new LogEntry(
            _timeProvider.GetUtcNow(),
            logLevel,
            category,
            eventId,
            message,
            exception);
        _entries.Enqueue(entry);
    }

    /// <summary>
    /// Clears all buffered log entries.
    /// </summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// Formats all log entries as a string.
    /// </summary>
    public string FormatAllEntries() => FormatEntries(_entries);

    /// <summary>
    /// Formats log entries at or above the specified minimum level as a string.
    /// </summary>
    public string FormatEntries(LogLevel minimumLevel) => FormatEntries(GetEntries(minimumLevel));

    /// <summary>
    /// Gets the approximate size in bytes of all formatted log entries.
    /// </summary>
    public long ApproximateSizeBytes
    {
        get
        {
            // Estimate size based on entries - this is approximate but avoids
            // formatting all entries just to check size
            long size = 0;
            foreach (var entry in _entries)
            {
                // Estimate: timestamp(30) + level(10) + category(50) + message + exception
                size += 90 + (entry.Message?.Length ?? 0) * 2; // UTF-16 chars
                if (entry.Exception != null)
                {
                    size += 500; // Rough estimate for exception text
                }
            }

            return size;
        }
    }

    /// <summary>
    /// Formats the actual entries and returns both the formatted string and its byte size.
    /// </summary>
    public (string Content, long SizeBytes) FormatAllEntriesWithSize()
    {
        var content = FormatAllEntries();
        var sizeBytes = Encoding.UTF8.GetByteCount(content);
        return (content, sizeBytes);
    }

    /// <summary>
    /// Formats entries at or above the specified level and returns both the formatted string and its byte size.
    /// </summary>
    public (string Content, long SizeBytes) FormatEntriesWithSize(LogLevel minimumLevel)
    {
        var content = FormatEntries(minimumLevel);
        var sizeBytes = Encoding.UTF8.GetByteCount(content);
        return (content, sizeBytes);
    }

    /// <summary>
    /// Asserts that no warnings or errors were logged, throwing an exception if any exist.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when warnings or errors exist in the log.</exception>
    public void AssertNoWarningsOrErrors()
    {
        var issues = GetEntries(LogLevel.Warning).ToList();
        if (issues.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Found {issues.Count} warnings/errors:");
            foreach (var entry in issues.Take(10))
            {
                sb.AppendLine(FormatEntry(entry));
            }
            if (issues.Count > 10)
            {
                sb.AppendLine($"... and {issues.Count - 10} more.");
            }
            throw new InvalidOperationException(sb.ToString());
        }
    }

    private static string FormatEntries(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.AppendLine(FormatEntry(entry));
        }

        return sb.ToString();
    }

    private static string FormatEntry(LogEntry entry)
    {
        var levelStr = entry.LogLevel switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            LogLevel.Critical => "CRIT",
            _ => "NONE",
        };

        var prefix = entry.LogLevel == LogLevel.Error ? "!!!!!!!!!! " : "";
        var exc = entry.Exception != null ? $"\n{PrintException(entry.Exception)}" : "";

        return string.Create(CultureInfo.InvariantCulture, $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Environment.CurrentManagedThreadId}\t{levelStr}\t{entry.EventId}\t{entry.Category}]\t{prefix}{entry.Message}{exc}");
    }

    private static string PrintException(Exception? exception)
    {
        if (exception == null)
            return "";

        var sb = new StringBuilder();
        PrintException(sb, exception, 0);
        return sb.ToString();
    }

    private static void PrintException(StringBuilder sb, Exception exception, int level)
    {
        if (exception == null) return;

        sb.Append(CultureInfo.InvariantCulture, $"Exc level {level}: {exception.GetType()}: {exception.Message}");

        if (exception.StackTrace is { } stack)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}{stack}");
        }

        if (exception is ReflectionTypeLoadException typeLoadException)
        {
            var loaderExceptions = typeLoadException.LoaderExceptions;
            if (loaderExceptions == null || loaderExceptions.Length == 0)
            {
                sb.Append("No LoaderExceptions found");
            }
            else
            {
                foreach (var inner in loaderExceptions)
                {
                    if (inner is not null)
                    {
                        PrintException(sb, inner, level + 1);
                    }
                }
            }
        }
        else if (exception.InnerException != null)
        {
            if (exception is AggregateException { InnerExceptions: { Count: > 1 } innerExceptions })
            {
                foreach (var inner in innerExceptions)
                {
                    PrintException(sb, inner, level + 1);
                }
            }
            else
            {
                PrintException(sb, exception.InnerException, level + 1);
            }
        }
    }
}

/// <summary>
/// A logger provider that buffers log messages in-memory for later retrieval.
/// Useful for tests where logs need to be attached to test results conditionally.
/// </summary>
public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly bool _ownsBuffer;
    private bool _disposed;

    /// <summary>
    /// Creates a new in-memory logger provider with its own buffer.
    /// </summary>
    /// <param name="timeProvider">Optional time provider for timestamps. Defaults to system time.</param>
    public InMemoryLoggerProvider(TimeProvider? timeProvider = null)
    {
        Buffer = new InMemoryLogBuffer(timeProvider);
        _ownsBuffer = true;
    }

    /// <summary>
    /// Creates a new in-memory logger provider that writes to a shared buffer.
    /// Multiple providers can share the same buffer to aggregate logs from multiple hosts.
    /// </summary>
    /// <param name="sharedBuffer">The shared buffer to write logs to.</param>
    public InMemoryLoggerProvider(InMemoryLogBuffer sharedBuffer)
    {
        ArgumentNullException.ThrowIfNull(sharedBuffer);
        Buffer = sharedBuffer;
        _ownsBuffer = false;
    }

    /// <summary>
    /// Gets the log buffer containing all logged entries.
    /// </summary>
    public InMemoryLogBuffer Buffer { get; }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, Buffer);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Only clear the buffer if we own it
        if (_ownsBuffer)
        {
            Buffer.Clear();
        }
    }
}

/// <summary>
/// A logger that writes to an in-memory buffer via the InMemoryLogBuffer.
/// </summary>
public sealed class InMemoryLogger(string categoryName, InMemoryLogBuffer buffer) : ILogger
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => NullScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        buffer.Log(logLevel, eventId, state, exception, formatter, categoryName);
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new NullScope();

        private NullScope()
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Represents a single log entry with its associated metadata.
/// </summary>
public readonly record struct LogEntry(
    DateTimeOffset Timestamp,
    LogLevel LogLevel,
    string Category,
    EventId EventId,
    string Message,
    Exception? Exception);
