using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderRecoveryReadTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StalledPageRead_ReleasesRecovery_AndRetriesIncompleteRange(bool cancel, bool continuationPage)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var table = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var stalledPage = new TaskCompletionSource<ReminderTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = new List<(uint Begin, uint End, string? Token)>();
        var retryEntry = CreateEntry("retry");
        var retry = false;
        var retryReads = 0;
        table.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>())
            .Returns(call =>
            {
                reads.Add((call.ArgAt<uint>(0), call.ArgAt<uint>(1), call.ArgAt<string?>(3)));
                if (retry)
                {
                    return Task.FromResult(++retryReads == 1 ? new ReminderTableData([retryEntry]) : new ReminderTableData());
                }

                return continuationPage && reads.Count == 1
                    ? Task.FromResult(new ReminderTableData([], "next-page"))
                    : stalledPage.Task;
            });
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var factory = Substitute.For<IGrainFactory>();
        factory.GetGrain<IAdvancedReminderDispatcherGrain>(Arg.Any<string>(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(table, factory, NullLogger<AdvancedReminderRecoveryGrain>.Instance, clock);
        using var cancellation = new CancellationTokenSource();

        var scan = recovery.StartAsync(force: true, cancellation.Token);
        try
        {
            Assert.False(scan.IsCompleted);
            Assert.Equal(continuationPage ? 2 : 1, reads.Count);
            var interruptedRange = reads[^1];
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            else
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                await Assert.ThrowsAsync<TimeoutException>(() => scan)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }

            // Late pages from an abandoned read must not schedule their stale rows.
            stalledPage.SetResult(new ReminderTableData([CreateEntry("stale")]));
            retry = true;

            await recovery.StartAsync(force: false, TestContext.Current.CancellationToken);

            var retriedRange = reads[continuationPage ? 2 : 1];
            Assert.Equal(interruptedRange.Begin, retriedRange.Begin);
            Assert.Equal(interruptedRange.End, retriedRange.End);
            Assert.Null(retriedRange.Token);
            Assert.Equal(AdvancedReminderRecoveryGrain.ScanBucketsPerReconciliation, retryReads);
            await dispatcher.Received(1).EnsureScheduledAsync(
                retryEntry.GrainId, retryEntry.ReminderName, retryEntry.ScheduleId, force: true, Arg.Any<CancellationToken>());
            Assert.Single(dispatcher.ReceivedCalls());
        }
        finally
        {
            stalledPage.TrySetResult(new ReminderTableData());
        }
    }

    private static ReminderEntry CreateEntry(string key) => new()
    {
        GrainId = GrainId.Create("test", key),
        ReminderName = key,
        StartAt = new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc),
        ScheduleId = $"schedule-{key}",
    };
}
