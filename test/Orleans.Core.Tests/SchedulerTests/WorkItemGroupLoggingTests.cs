using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Xunit;

namespace UnitTests.SchedulerTests;

[TestCategory("BVT"), TestCategory("Scheduler")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class WorkItemGroupLoggingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredTaskValuesAreStringsAndQueuedWorkCompletes(bool useTaskState)
    {
        using var provider = new TaskValueLoggerProvider(warningsEnabled: true);
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        using var context = UnitTestSchedulingContext.Create(loggerFactory);
        context.ActivationServices.GetRequiredService<IOptions<SchedulingOptions>>().Value.TurnWarningLengthThreshold = TimeSpan.FromMilliseconds(-1);
        var incomplete = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = useTaskState ? incomplete.Task : null;
        var task = new Task<int>(_ => 42, state);

        context.WorkItemGroup.QueueTask(task);
        context.WorkItemGroup.QueueAction(() => provider.FollowingTurnStarted = true);
        await provider.FollowingTurnChecked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(42, await task);
        Assert.False(incomplete.Task.IsCompleted);
        var values = provider.TaskValues.ToArray();
        Assert.NotEmpty(values);
        Assert.All(values, value => Assert.IsType<string>(value.Value));
        Assert.Contains(values, value => value.EventId.Id == (int)ErrorCode.SchedulerTurnTooLong3
            && Equals(value.Value, (state ?? (object)task).ToString()));
#if DEBUG
        Assert.Contains(values, value => value.Template.StartsWith("EnqueueWorkItem ", StringComparison.Ordinal));
        Assert.Contains(values, value => value.Template.StartsWith("Add to RunQueue ", StringComparison.Ordinal));
        Assert.Contains(values, value => value.Template.StartsWith("About to execute task ", StringComparison.Ordinal));
#endif
    }

    [Fact]
    public async Task DisabledWarningsDoNotFormatTaskState()
    {
        using var provider = new TaskValueLoggerProvider(warningsEnabled: false);
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        using var context = UnitTestSchedulingContext.Create(loggerFactory);
        context.ActivationServices.GetRequiredService<IOptions<SchedulingOptions>>().Value.TurnWarningLengthThreshold = TimeSpan.FromMilliseconds(-1);
        var state = new ThrowingTaskState();
        var task = new Task<int>(_ => 42, state);

        context.WorkItemGroup.QueueTask(task);
        context.WorkItemGroup.QueueAction(() => provider.FollowingTurnStarted = true);
        await provider.FollowingTurnChecked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(42, await task);
        Assert.Equal(0, state.FormatCount);
        Assert.DoesNotContain(provider.TaskValues, value => value.EventId.Id == (int)ErrorCode.SchedulerTurnTooLong3);
    }

    private sealed class ThrowingTaskState
    {
        public int FormatCount;

        public override string ToString()
        {
            Interlocked.Increment(ref FormatCount);
            throw new InvalidOperationException("Disabled warning logging must not format task state.");
        }
    }

    private sealed class TaskValueLoggerProvider(bool warningsEnabled) : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(EventId EventId, string Template, object? Value)> TaskValues { get; } = new();
        public TaskCompletionSource FollowingTurnChecked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FollowingTurnStarted { get; set; }

        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel)
        {
            // Wait until the final turn has acquired its logger before disposing activation services.
            if (logLevel == LogLevel.Warning && FollowingTurnStarted)
            {
                FollowingTurnChecked.TrySetResult();
            }

            return logLevel != LogLevel.Warning || warningsEnabled;
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Dispose() { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
            {
                return;
            }

            var values = properties.ToArray();
            var template = values.First(value => value.Key == "{OriginalFormat}").Value as string ?? "";
            foreach (var value in values)
            {
                if (value.Key == "Task")
                {
                    TaskValues.Enqueue((eventId, template, value.Value));
                }
            }
        }
    }
}
