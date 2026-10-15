using Microsoft.Extensions.Logging;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>Tests that <see cref="DurableTaskLoggerExtensions.ReplaySafe(ILogger)"/> reports each event of a task once.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskLoggerExtensionsTests
{
    [Fact]
    public async Task ReplaySafeLoggerSkipsEventsWhileTheTaskReplays()
    {
        var inner = new RecordingLogger();
        var logger = inner.ReplaySafe();
        var replaying = new ReplayContext(TaskId.CreateRoot("root"), isReplaying: true);
        var live = new ReplayContext(TaskId.CreateRoot("root"), isReplaying: false);

        var replayed = await RunAsync(replaying, () => Log(logger, "replayed"));
        var logged = await RunAsync(live, () => Log(logger, "live"));
        var outside = Log(logger, "outside");

        Assert.False(replayed);
        Assert.True(logged);
        Assert.True(outside);
        Assert.Equal(["live", "outside"], inner.Messages);
    }

    [Fact]
    public void ReplaySafeWrapsOnceAndBeginsScopesOnTheLogger()
    {
        var inner = new RecordingLogger();
        var logger = inner.ReplaySafe();

        Assert.Same(logger, logger.ReplaySafe());
        using (logger.BeginScope("scope"))
        {
            Assert.Equal(["scope"], inner.Scopes);
        }

        Assert.Throws<ArgumentNullException>(() => DurableTaskLoggerExtensions.ReplaySafe(null!));
    }

    private static bool Log(ILogger logger, string message)
    {
        var enabled = logger.IsEnabled(LogLevel.Information);
        logger.Log(LogLevel.Information, default, message, null, static (state, _) => state);
        return enabled;
    }

    private static async Task<T> RunAsync<T>(DurableTaskContext context, Func<T> body)
        => (await DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(_ => body()), context)).GetResult<T>();

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public List<object> Scopes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scopes.Add(state);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class ReplayContext(TaskId taskId, bool isReplaying) : DurableTaskContext(taskId)
    {
        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public override bool IsReplaying => isReplaying;

        protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(TaskId taskId, DurableTask taskDefinition, DurableTaskStartOptions options, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId) => throw new NotSupportedException();

        protected override ValueTask<TaskId> SelectCompletionAsync(TaskId decisionId, IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
