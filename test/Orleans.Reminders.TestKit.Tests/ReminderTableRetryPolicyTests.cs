using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

public class ReminderTableRetryPolicyTests
{
    [Fact]
    public async Task SharedRunner_RetriesTransientTableContentionAndEventualReads()
    {
        var table = new TransientReminderTable();
        var runner = new TestRunner(table, "TransientTable");

        await runner.RunReminderTable_UpsertRow_PersistsScheduleForPointRead(TestContext.Current.CancellationToken);

        Assert.Equal(3, table.UpsertAttempts);
        Assert.Equal(2, table.HiddenPointReads);
    }

    [Fact]
    public async Task UniformReadPolicy_RetriesEventualConsistency()
    {
        var attempts = 0;

        var result = await ReminderTableRetryPolicy.ExecuteUntilAsync(
            () => Task.FromResult(++attempts),
            value => value == 3,
            "EventuallyConsistent",
            "ReadGuarantee",
            "ReadRow",
            "the third observation",
            value => value.ToString(),
            "read convergence",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task UniformMutationPolicy_RetriesTransientNullAndExceptionResponses()
    {
        var attempts = 0;

        var result = await ReminderTableRetryPolicy.ExecuteUntilAsync<string?>(
            () =>
            {
                attempts++;
                return attempts switch
                {
                    1 => Task.FromResult<string?>(null),
                    2 => Task.FromException<string?>(new InvalidOperationException("transient contention")),
                    _ => Task.FromResult<string?>("etag-3")
                };
            },
            value => !string.IsNullOrEmpty(value),
            "Contended",
            "MutationGuarantee",
            "UpsertRow",
            "a non-empty ETag",
            value => value ?? "<null>",
            "mutation retry",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal("etag-3", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task UniformReadPolicy_TimeoutReportsLastObservation()
    {
        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(() =>
            ReminderTableRetryPolicy.ExecuteUntilAsync(
                () => Task.FromResult("stale-row"),
                value => value == "current-row",
                "EventuallyConsistent",
                "ReadGuarantee",
                "ReadRow",
                "current-row",
                value => value,
                "read convergence",
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(5),
                TestContext.Current.CancellationToken));

        Assert.Contains("provider=EventuallyConsistent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("operation=ReadRow", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Last observation: stale-row", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Last exception: <none>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniformMutationPolicy_DetectsPersistentContentionWithLastException()
    {
        var attempts = 0;
        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(() =>
            ReminderTableRetryPolicy.ExecuteUntilAsync<string?>(
                () =>
                {
                    attempts++;
                    return Task.FromException<string?>(new InvalidOperationException($"contention-{attempts}"));
                },
                value => !string.IsNullOrEmpty(value),
                "PersistentlyContended",
                "MutationGuarantee",
                "UpsertRow",
                "a non-empty ETag",
                value => value ?? "<null>",
                "mutation retry",
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(5),
                TestContext.Current.CancellationToken));

        Assert.True(attempts > 1);
        Assert.Contains("provider=PersistentlyContended", exception.Message, StringComparison.Ordinal);
        Assert.Contains("mutation retry timed out", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Last observation: <no completed attempt>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Last exception: System.InvalidOperationException: contention-", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniformPolicy_HardBoundsTheFirstAttempt()
    {
        var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(() =>
            ReminderTableRetryPolicy.ExecuteUntilAsync(
                () =>
                {
                    invocations++;
                    return never.Task;
                },
                _ => true,
                "Blocked",
                "ReadGuarantee",
                "ReadRow",
                "a completed read",
                value => value,
                "read convergence",
                TimeSpan.FromTicks(1),
                TimeSpan.FromMilliseconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, invocations);
        Assert.Contains("attempts=1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Last exception: System.TimeoutException", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniformPolicy_EnforcesDeadlineAfterStartingFirstAttempt()
    {
        var invocations = 0;

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(() =>
            ReminderTableRetryPolicy.ExecuteUntilAsync(
                () =>
                {
                    invocations++;
                    Thread.Sleep(TimeSpan.FromMilliseconds(20));
                    return Task.FromResult("late-success");
                },
                _ => true,
                "DelayedStart",
                "ReadGuarantee",
                "ReadRow",
                "a result within the deadline",
                value => value,
                "read convergence",
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, invocations);
        Assert.Contains("attempts=1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Last observation: <no completed attempt>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Last exception: System.TimeoutException", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TestRunner(IReminderTable table, string providerName)
        : ReminderTableTestRunner(table, providerName);

    private sealed class TransientReminderTable : IReminderTable
    {
        private readonly IdealizedReminderTable _inner = new(nameof(TransientReminderTable));
        private int _remainingHiddenPointReads = 2;

        public int UpsertAttempts { get; private set; }

        public int HiddenPointReads { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

        public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
        {
            var result = await _inner.ReadRow(grainId, reminderName);
            if (result is not null && Interlocked.Decrement(ref _remainingHiddenPointReads) >= 0)
            {
                HiddenPointReads++;
                return null;
            }

            return result;
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId) => _inner.ReadRows(grainId);

        public Task<ReminderTableData> ReadRows(uint begin, uint end) => _inner.ReadRows(begin, end);

        public Task<string?> UpsertRow(ReminderEntry entry)
        {
            UpsertAttempts++;
            return UpsertAttempts switch
            {
                1 => Task.FromException<string?>(new InvalidOperationException("transient contention")),
                2 => Task.FromResult<string?>(null),
                _ => _inner.UpsertRow(entry)
            };
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
            => _inner.RemoveRow(grainId, reminderName, eTag);

        public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();
    }
}
