using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost.Logging;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public sealed class InMemoryLoggerProviderTests
{
    [Fact]
    public void FormatAllEntries_NestedException_FormatsMetadataAndEveryLevelExactly()
    {
        var inner = new FormatException("inner failure");
        var middle = new ArgumentException("middle failure", inner);
        var outer = new InvalidOperationException("outer failure", middle);
        var buffer = new InMemoryLogBuffer();
        using var provider = new InMemoryLoggerProvider(buffer);
        var logger = provider.CreateLogger("Nested.Category");

        logger.Log(
            LogLevel.Error,
            new EventId(101, "NestedEvent"),
            "nested message",
            outer,
            static (state, _) => state);

        var entry = Assert.Single(buffer.AllEntries);
        var exceptionText =
            FormatException(0, outer)
            + FormatException(1, middle)
            + FormatException(2, inner);
        var expected = FormatEntry(entry, exceptionText) + Environment.NewLine;

        Assert.Equal(expected, buffer.FormatAllEntries());
    }

    [Fact]
    public void FormatAllEntries_AggregateException_FormatsAllInnerExceptionsAtSameDepth()
    {
        var first = new InvalidOperationException("first failure");
        var second = new ArgumentException("second failure");
        var aggregate = new AggregateException("aggregate failure", first, second);
        var buffer = new InMemoryLogBuffer();
        using var provider = new InMemoryLoggerProvider(buffer);
        var logger = provider.CreateLogger("Aggregate.Category");

        logger.Log(
            LogLevel.Warning,
            new EventId(102, "AggregateEvent"),
            "aggregate message",
            aggregate,
            static (state, _) => state);

        var entry = Assert.Single(buffer.AllEntries);
        var exceptionText =
            FormatException(0, aggregate)
            + FormatException(1, first)
            + FormatException(1, second);
        var expected = FormatEntry(entry, exceptionText) + Environment.NewLine;

        Assert.Equal(expected, buffer.FormatAllEntries());
    }

    [Fact]
    public void FormatAllEntries_ReflectionTypeLoadException_FormatsLoaderExceptionDiagnostics()
    {
        var first = new TypeLoadException("loader one");
        var second = new FileNotFoundException("loader two");
        var exception = new ReflectionTypeLoadException([], [first, second]);
        var buffer = new InMemoryLogBuffer();
        using var provider = new InMemoryLoggerProvider(buffer);
        var logger = provider.CreateLogger("Loader.Category");

        logger.Log(
            LogLevel.Critical,
            new EventId(103, "LoaderEvent"),
            "loader message",
            exception,
            static (state, _) => state);

        var entry = Assert.Single(buffer.AllEntries);
        var exceptionText =
            FormatException(0, exception)
            + FormatException(1, first)
            + FormatException(1, second);
        var expected = FormatEntry(entry, exceptionText) + Environment.NewLine;

        Assert.Equal(expected, buffer.FormatAllEntries());
    }

    [Fact]
    public void Log_NoneAndMinimumLevelFiltering_ReturnsOnlyExpectedEntries()
    {
        var buffer = new InMemoryLogBuffer();
        using var provider = new InMemoryLoggerProvider(buffer);
        var logger = provider.CreateLogger("Filter.Category");
        var levels = new[]
        {
            LogLevel.Trace,
            LogLevel.Debug,
            LogLevel.Information,
            LogLevel.Warning,
            LogLevel.Error,
            LogLevel.Critical,
            LogLevel.None,
        };

        foreach (var level in levels)
        {
            logger.Log(
                level,
                new EventId(200 + (int)level, $"Event-{level}"),
                $"message-{level}",
                exception: null,
                static (state, _) => state);
        }

        Assert.All(levels[..^1], level => Assert.True(logger.IsEnabled(level)));
        Assert.False(logger.IsEnabled(LogLevel.None));
        Assert.Equal(
            [
                "Trace|200|Event-Trace|Filter.Category|message-Trace",
                "Debug|201|Event-Debug|Filter.Category|message-Debug",
                "Information|202|Event-Information|Filter.Category|message-Information",
                "Warning|203|Event-Warning|Filter.Category|message-Warning",
                "Error|204|Event-Error|Filter.Category|message-Error",
                "Critical|205|Event-Critical|Filter.Category|message-Critical",
            ],
            buffer.AllEntries.Select(DescribeEntry));
        Assert.Equal(
            [
                "Warning|203|Event-Warning|Filter.Category|message-Warning",
                "Error|204|Event-Error|Filter.Category|message-Error",
                "Critical|205|Event-Critical|Filter.Category|message-Critical",
            ],
            buffer.GetEntries(LogLevel.Warning).Select(DescribeEntry));
    }

    [Fact]
    public void BeginScope_IsNoOpAndDoesNotSuppressFollowingLogEntry()
    {
        var buffer = new InMemoryLogBuffer();
        using var provider = new InMemoryLoggerProvider(buffer);
        var logger = provider.CreateLogger("Scope.Category");

        var outer = Assert.IsAssignableFrom<IDisposable>(logger.BeginScope("outer-scope"));
        var inner = Assert.IsAssignableFrom<IDisposable>(logger.BeginScope("inner-scope"));
        Assert.Same(outer, inner);
        inner.Dispose();
        outer.Dispose();
        logger.Log(
            LogLevel.Information,
            new EventId(301, "AfterScopes"),
            "scope-free message",
            exception: null,
            static (state, _) => state);

        var entry = Assert.Single(buffer.AllEntries);
        Assert.Equal("Information|301|AfterScopes|Scope.Category|scope-free message", DescribeEntry(entry));
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task ConcurrentLog_AllEntriesAreCollectedExactlyOnce()
    {
        const int workerCount = 16;
        var buffer = new InMemoryLogBuffer();
        using var provider = new InMemoryLoggerProvider(buffer);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = Enumerable.Range(0, workerCount)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

        var workers = Enumerable.Range(0, workerCount)
            .Select(LogAfterReleaseAsync)
            .ToArray();
        await Task.WhenAll(armed.Select(static signal => signal.Task))
            .WaitAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(workers).WaitAsync(TestContext.Current.CancellationToken);

        var entries = buffer.AllEntries;
        Assert.Equal(workerCount, entries.Count);
        Assert.Equal(
            Enumerable.Range(0, workerCount).Select(static index =>
                $"Concurrent.Category.{index:D2}|{400 + index}:ConcurrentEvent{index:D2}|concurrent-message-{index:D2}"),
            entries
                .Select(static entry =>
                    $"{entry.Category}|{entry.EventId.Id}:{entry.EventId.Name}|{entry.Message}")
                .Order());

        async Task LogAfterReleaseAsync(int index)
        {
            var logger = provider.CreateLogger($"Concurrent.Category.{index:D2}");
            armed[index].SetResult();
            await release.Task;
            logger.Log(
                LogLevel.Information,
                new EventId(400 + index, $"ConcurrentEvent{index:D2}"),
                $"concurrent-message-{index:D2}",
                exception: null,
                static (state, _) => state);
        }
    }

    [Fact]
    public void Dispose_ClearsOwnedBufferButPreservesSharedBuffer()
    {
        var ownedProvider = new InMemoryLoggerProvider();
        var ownedLogger = ownedProvider.CreateLogger("Owned.Category");
        LogMessage(ownedLogger, 501, "owned-before-dispose");

        ownedProvider.Dispose();

        Assert.Empty(ownedProvider.Buffer.AllEntries);
        LogMessage(ownedLogger, 502, "owned-after-first-dispose");
        ownedProvider.Dispose();
        Assert.Equal(["owned-after-first-dispose"], ownedProvider.Buffer.AllEntries.Select(static entry => entry.Message));

        var sharedBuffer = new InMemoryLogBuffer();
        var sharedProvider = new InMemoryLoggerProvider(sharedBuffer);
        var sharedLogger = sharedProvider.CreateLogger("Shared.Category");
        LogMessage(sharedLogger, 503, "shared-before-dispose");

        sharedProvider.Dispose();

        Assert.Equal(["shared-before-dispose"], sharedBuffer.AllEntries.Select(static entry => entry.Message));
        LogMessage(sharedLogger, 504, "shared-after-first-dispose");
        sharedProvider.Dispose();
        Assert.Equal(
            ["shared-before-dispose", "shared-after-first-dispose"],
            sharedBuffer.AllEntries.Select(static entry => entry.Message));
    }

    [Fact]
    public void AssertNoWarningsOrErrors_ReportsFirstTenEntriesAndRemainderExactly()
    {
        var buffer = new InMemoryLogBuffer();
        using var provider = new InMemoryLoggerProvider(buffer);
        var logger = provider.CreateLogger("Diagnostics.Category");
        LogMessage(logger, 600, "ignored-information", LogLevel.Information);
        for (var index = 0; index < 12; index++)
        {
            LogMessage(
                logger,
                601 + index,
                $"issue-{index:D2}",
                index % 2 == 0 ? LogLevel.Warning : LogLevel.Error);
        }

        var issues = buffer.GetEntries(LogLevel.Warning).ToArray();
        var expected = new StringBuilder()
            .AppendLine("Found 12 warnings/errors:");
        foreach (var entry in issues.Take(10))
        {
            expected.AppendLine(FormatEntry(entry));
        }

        expected.AppendLine("... and 2 more.");

        var exception = Assert.Throws<InvalidOperationException>(buffer.AssertNoWarningsOrErrors);

        Assert.Equal(expected.ToString(), exception.Message);
    }

    [Fact]
    public void FormatEntriesWithSize_ReturnsExactUtf8ByteCount()
    {
        var buffer = new InMemoryLogBuffer();
        using var provider = new InMemoryLoggerProvider(buffer);
        var logger = provider.CreateLogger("Size.Category");
        LogMessage(logger, 701, "excluded café", LogLevel.Information);
        LogMessage(logger, 702, "naïve 東京 ☕", LogLevel.Warning);

        var included = Assert.Single(buffer.GetEntries(LogLevel.Warning));
        var expectedContent = FormatEntry(included) + Environment.NewLine;

        var (content, sizeBytes) = buffer.FormatEntriesWithSize(LogLevel.Warning);

        Assert.Equal(expectedContent, content);
        Assert.Equal(Encoding.UTF8.GetByteCount(expectedContent), sizeBytes);
        Assert.True(sizeBytes > content.Length);
    }

    private static void LogMessage(
        ILogger logger,
        int eventId,
        string message,
        LogLevel level = LogLevel.Information) =>
        logger.Log(
            level,
            new EventId(eventId, $"Event-{eventId}"),
            message,
            exception: null,
            static (state, _) => state);

    private static string DescribeEntry(LogEntry entry) =>
        $"{entry.LogLevel}|{entry.EventId.Id}|{entry.EventId.Name}|{entry.Category}|{entry.Message}";

    private static string FormatException(int level, Exception exception) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Exc level {level}: {exception.GetType()}: {exception.Message}");

    private static string FormatEntry(LogEntry entry, string? exceptionText = null)
    {
        var level = entry.LogLevel switch
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
        var exception = exceptionText is null ? "" : $"\n{exceptionText}";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} {entry.ThreadId}\t{level}\t{entry.EventId}\t{entry.Category}]\t{prefix}{entry.Message}{exception}");
    }
}
