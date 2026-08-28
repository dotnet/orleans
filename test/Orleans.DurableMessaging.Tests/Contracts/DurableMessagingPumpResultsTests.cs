using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using Orleans.DurableJobs;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableMessagingPumpResultsTests
{
    [Fact]
    public void ConcurrentStarts_SuppressDuplicateExecution()
    {
        var results = new PumpResults();
        var key = results.CreateKey("job", "id", "run");
        var starts = new bool[64];

        Parallel.For(0, starts.Length, index => starts[index] = results.TryStart(key, out _));

        Assert.Equal(1, starts.Count(static started => started));
    }

    [Fact]
    public void CanceledWaitingExecution_BecomesTakeableAndDoesNotRun()
    {
        var results = new PumpResults();
        var key = results.CreateKey("job", "id", "run");
        using var cancellation = new CancellationTokenSource();

        Assert.True(results.TryStartWithCancellation(key, out var execution, cancellation.Token));
        cancellation.Cancel();

        Assert.False(results.TryBegin(execution));
        Assert.True(results.TryTake(key, out var result, out var exception));
        Assert.Null(result);
        Assert.IsType<OperationCanceledException>(exception);
    }

    [Fact]
    public void CompletedResultWithoutSecondPoll_Expires()
    {
        var clock = new FakeTimeProvider();
        var results = new PumpResults(clock, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 16);
        var key = results.CreateKey("job", "id", "run");
        Assert.True(results.TryStart(key, out var execution, TestContext.Current.CancellationToken));
        Assert.True(results.TryBegin(execution));
        results.Complete(execution);

        clock.Advance(TimeSpan.FromMinutes(2));
        _ = results.TryStart(results.CreateKey("job", "other", "run"), out _, TestContext.Current.CancellationToken);

        Assert.False(results.TryTake(key, out _, out _));
    }

    [Fact]
    public void RetainedEntries_AreBounded()
    {
        var results = new PumpResults(new FakeTimeProvider(), TimeSpan.FromHours(1), TimeSpan.FromHours(1), 4);

        for (var index = 0; index < 100; index++)
        {
            Assert.True(results.TryStart(
                results.CreateKey("job", index.ToString(), "run"),
                out _,
                TestContext.Current.CancellationToken));
        }

        Assert.InRange(results.Count, 0, 4);
    }

    [Fact]
    public void RunningExecution_IsNotExpiredOrDuplicated()
    {
        var clock = new FakeTimeProvider();
        var results = new PumpResults(clock, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 4);
        var key = results.CreateKey("job", "id", "run");
        Assert.True(results.TryStart(key, out var execution, TestContext.Current.CancellationToken));
        Assert.True(results.TryBegin(execution));

        clock.Advance(TimeSpan.FromHours(1));

        Assert.False(results.TryStart(key, out _, TestContext.Current.CancellationToken));
        results.Complete(execution);
        Assert.True(results.TryTake(key, out var result, out var exception));
        Assert.Same(DurableJobRunResult.Completed, result);
        Assert.Null(exception);
    }

    [Fact]
    public void CapacityExhaustedByRunningExecution_RejectsNewStartWithoutLosingRunningExecution()
    {
        var results = new PumpResults(new FakeTimeProvider(), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 1);
        var runningKey = results.CreateKey("job", "running", "run");
        var rejectedKey = results.CreateKey("job", "rejected", "run");
        Assert.True(results.TryStart(runningKey, out var execution));
        Assert.True(results.TryBegin(execution));

        Assert.False(results.TryStart(rejectedKey, out _));
        results.Complete(execution);

        Assert.True(results.TryTake(runningKey, out var result, out var exception));
        Assert.Same(DurableJobRunResult.Completed, result);
        Assert.Null(exception);
    }

    [Fact]
    public void DifferentRunId_DoesNotObserveOlderResult()
    {
        var results = new PumpResults();
        var firstKey = results.CreateKey("job", "id", "run-1");
        var secondKey = results.CreateKey("job", "id", "run-2");
        Assert.True(results.TryStart(firstKey, out var firstExecution, TestContext.Current.CancellationToken));
        Assert.True(results.TryBegin(firstExecution));
        results.Complete(firstExecution);

        Assert.True(results.TryStart(secondKey, out var secondExecution, TestContext.Current.CancellationToken));
        Assert.False(results.TryTake(secondKey, out _, out _));
        Assert.True(results.TryTake(firstKey, out var firstResult, out _));
        Assert.Same(DurableJobRunResult.Completed, firstResult);
        Assert.True(results.TryBegin(secondExecution));
    }

    private sealed class PumpResults
    {
        private static readonly Assembly Assembly = typeof(IDurableOutbox).Assembly;
        private static readonly Type ResultsType = Assembly.GetType("Orleans.DurableMessaging.DurableMessagingPumpResults", throwOnError: true)!;
        private static readonly Type KeyType = Assembly.GetType("Orleans.DurableMessaging.DurableMessagingPumpExecutionKey", throwOnError: true)!;
        private readonly object _instance;

        public PumpResults()
        {
            _instance = Activator.CreateInstance(ResultsType, nonPublic: true)!;
        }

        public PumpResults(TimeProvider timeProvider, TimeSpan completedRetention, TimeSpan abandonedRetention, int maxEntries)
        {
            _instance = Activator.CreateInstance(
                ResultsType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [timeProvider, completedRetention, abandonedRetention, maxEntries],
                culture: null)!;
        }

        public int Count => ((IDictionary)ResultsType
            .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_instance)!).Count;

        public object CreateKey(string jobName, string jobId, string runId) =>
            Activator.CreateInstance(KeyType, [jobName, jobId, runId])!;

        public bool TryStart(object key, out object execution) =>
            TryStartWithCancellation(key, out execution, TestContext.Current.CancellationToken);

        public bool TryStartWithCancellation(
            object key,
            out object execution,
            CancellationToken cancellationToken)
        {
            object?[] arguments = [key, cancellationToken, null];
            var result = (bool)ResultsType.GetMethod("TryStart")!.Invoke(_instance, arguments)!;
            execution = arguments[2]!;
            return result;
        }

        public bool TryBegin(object execution) =>
            (bool)ResultsType.GetMethod("TryBegin")!.Invoke(_instance, [execution])!;

        public void Complete(object execution) =>
            ResultsType.GetMethod("Complete")!.Invoke(_instance, [execution, DurableJobRunResult.Completed]);

        public bool TryTake(object key, out DurableJobRunResult? result, out Exception? exception)
        {
            object?[] arguments = [key, null, null];
            var taken = (bool)ResultsType.GetMethod("TryTake")!.Invoke(_instance, arguments)!;
            result = arguments[1] as DurableJobRunResult;
            exception = arguments[2] as Exception;
            return taken;
        }
    }
}
