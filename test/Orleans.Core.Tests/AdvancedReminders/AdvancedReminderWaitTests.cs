using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderWaitTests : AdvancedReminderServiceTestBase
{
    [Theory]
    [InlineData("register")]
    [InlineData("attribute")]
    [InlineData("management")]
    [InlineData("unregister")]
    [InlineData("repair")]
    [InlineData("prepare")]
    [InlineData("complete")]
    [InlineData("get")]
    [InlineData("list")]
    public async Task StalledRead_TimesOut_WithoutApplyingItsLateResult(string operation)
        => await CheckReadAsync(operation, cancel: false, alreadyCanceled: false);

    public static IEnumerable<object[]> CanceledReads()
    {
        foreach (var operation in new[] { "register", "attribute", "management", "unregister", "repair", "prepare", "complete" })
            foreach (var alreadyCanceled in new[] { false, true })
            {
                yield return [operation, alreadyCanceled];
            }
    }

    [Theory]
    [MemberData(nameof(CanceledReads))]
    public async Task CanceledRead_DoesNotApplyItsLateResult(string operation, bool alreadyCanceled)
        => await CheckReadAsync(operation, cancel: true, alreadyCanceled);

    [Fact]
    public async Task CanceledDefinitionWrite_RemainsAwaitedUntilTheProviderFinishes()
    {
        var clock = new FakeTimeProvider();
        var write = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var table = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        table.UpsertRow(Arg.Any<ReminderEntry>()).Returns(write.Task);
        var jobs = Substitute.For<ILocalDurableJobManager>();
        var service = CreateService(table, jobManager: jobs, timeProvider: clock);
        var entry = CreateDueEntry(clock.GetUtcNow(), "pending-write");
        using var cancellation = new CancellationTokenSource();
        var operation = service.RegisterOrUpdateCoreAsync(entry, cancellation.Token);
        try
        {
            _ = table.Received(1).UpsertRow(Arg.Any<ReminderEntry>());
            cancellation.Cancel();
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.False(operation.IsCompleted);

            write.SetResult("committed-etag");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("committed-etag", entry.ETag);
            await jobs.DidNotReceive().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            write.TrySetCanceled(TestContext.Current.CancellationToken);
        }
    }

    private async Task CheckReadAsync(string operation, bool cancel, bool alreadyCanceled)
    {
        var clock = new FakeTimeProvider();
        var row = new TaskCompletionSource<ReminderEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = new TaskCompletionSource<ReminderTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var table = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        table.ReadRow(Arg.Any<GrainId>(), Arg.Any<string>()).Returns(row.Task);
        table.ReadRows(Arg.Any<GrainId>()).Returns(rows.Task);
        var jobs = Substitute.For<ILocalDurableJobManager>();
        var service = CreateService(table, jobManager: jobs, timeProvider: clock);
        var entry = CreateDueEntry(clock.GetUtcNow(), "stalled-read");
        using var cancellation = new CancellationTokenSource();
        if (alreadyCanceled) cancellation.Cancel();
        var pending = operation switch
        {
            "register" => service.RegisterOrUpdateCoreAsync(Clone(entry), cancellation.Token),
            "attribute" => service.ReconcileAttributeCoreAsync(Clone(entry), "declaration", cancellation.Token),
            "management" => service.UpsertAndScheduleCoreAsync(Clone(entry), cancellation.Token),
            "unregister" => service.UnregisterCoreAsync(Assert.IsType<Orleans.AdvancedReminders.ReminderData>(entry.ToIGrainReminder()), cancellation.Token),
            "repair" => service.EnsureScheduledCoreAsync(entry.GrainId, entry.ReminderName, entry.ScheduleId, force: true, cancellation.Token),
            "prepare" => service.PrepareReminderDeliveryCoreAsync(entry.GrainId, entry.ReminderName, entry.ScheduleId, 0, cancellation.Token),
            "complete" => service.CompleteReminderDeliveryCoreAsync(entry, clock.GetUtcNow().UtcDateTime, null, 0, cancellation.Token),
            "get" => service.GetReminder(entry.GrainId, entry.ReminderName),
            "list" => service.GetReminders(entry.GrainId),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        try
        {
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            else
            {
                Assert.False(pending.IsCompleted);
                clock.Advance(TimeSpan.FromMinutes(1));
                await Assert.ThrowsAsync<TimeoutException>(() => pending)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }

            row.SetResult(entry);
            rows.SetResult(new ReminderTableData([entry]));
            if (alreadyCanceled)
            {
                await table.DidNotReceive().ReadRow(Arg.Any<GrainId>(), Arg.Any<string>());
                await table.DidNotReceive().ReadRows(Arg.Any<GrainId>());
            }
            await table.DidNotReceive().UpsertRow(Arg.Any<ReminderEntry>());
            await table.DidNotReceive().RemoveRow(Arg.Any<GrainId>(), Arg.Any<string>(), Arg.Any<string>());
            await jobs.DidNotReceive().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            row.TrySetCanceled(TestContext.Current.CancellationToken);
            rows.TrySetCanceled(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("list-page")]
    [InlineData("list-for-grain")]
    [InlineData("set-action")]
    [InlineData("repair")]
    [InlineData("delete")]
    public async Task StalledManagementRead_TimesOut_WithoutChangingTheDefinition(string operation)
    {
        var clock = new FakeTimeProvider();
        var row = new TaskCompletionSource<ReminderEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = new TaskCompletionSource<ReminderTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var table = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        table.ReadRow(Arg.Any<GrainId>(), Arg.Any<string>()).Returns(row.Task);
        table.ReadRows(Arg.Any<GrainId>()).Returns(rows.Task);
        table.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<int>(), Arg.Any<string?>()).Returns(rows.Task);
        var management = new ReminderManagementGrain(table, serviceProvider: null, timeProvider: clock);
        var entry = CreateDueEntry(clock.GetUtcNow(), "management-read");
        var pending = operation switch
        {
            "list-page" => management.ListAllAsync(),
            "list-for-grain" => management.ListForGrainAsync(entry.GrainId),
            "set-action" => management.SetActionAsync(entry.GrainId, entry.ReminderName, Orleans.AdvancedReminders.Runtime.MissedReminderAction.Notify),
            "repair" => management.RepairAsync(entry.GrainId, entry.ReminderName),
            "delete" => management.DeleteAsync(entry.GrainId, entry.ReminderName),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        try
        {
            Assert.False(pending.IsCompleted);
            clock.Advance(TimeSpan.FromMinutes(1));
            await Assert.ThrowsAsync<TimeoutException>(() => pending)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            row.SetResult(entry);
            rows.SetResult(new ReminderTableData([entry]));
            await table.DidNotReceive().UpsertRow(Arg.Any<ReminderEntry>());
            await table.DidNotReceive().RemoveRow(Arg.Any<GrainId>(), Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            row.TrySetCanceled(TestContext.Current.CancellationToken);
            rows.TrySetCanceled(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StalledScheduling_ReleasesWait_AndRecoveryFencesTheLateJob(bool cancel, bool honorsCancellation)
    {
        var clock = new FakeTimeProvider();
        var entry = CreateDueEntry(clock.GetUtcNow(), "stalled-scheduling");
        entry.Action = Orleans.AdvancedReminders.Runtime.MissedReminderAction.FireImmediately;
        var table = new MutableReminderTable(null);
        var response = new TaskCompletionSource<DurableJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<ScheduleJobRequest>();
        CancellationToken schedulingToken = default;
        var jobs = Substitute.For<ILocalDurableJobManager>();
        jobs.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<ScheduleJobRequest>();
            requests.Add(request);
            if (requests.Count != 1) return Task.FromResult(CreateDurableJob(request));
            schedulingToken = call.Arg<CancellationToken>();
            return honorsCancellation ? response.Task.WaitAsync(schedulingToken) : response.Task;
        });
        var recipient = new CallbackRemindable(() => Task.CompletedTask);
        var factory = Substitute.For<IGrainFactory>();
        factory.GetGrain<Orleans.AdvancedReminders.IRemindable>(entry.GrainId).Returns(recipient);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "stalled-scheduling-dispatcher"));
        factory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null)
            .Returns(dispatcher);
        var service = CreateService(table, jobManager: jobs, grainFactory: factory, timeProvider: clock);
        using var cancellation = new CancellationTokenSource();
        var registration = service.RegisterOrUpdateCoreAsync(entry, cancellation.Token);
        try
        {
            Assert.False(registration.IsCompleted);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            else
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                await Assert.ThrowsAsync<TimeoutException>(() => registration)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            Assert.True(schedulingToken.IsCancellationRequested);
            var pending = Assert.IsType<ReminderEntry>(await table.ReadRow(entry.GrainId, entry.ReminderName));
            Assert.Empty(pending.JobId);
            Assert.Equal(requests[0].Metadata!["schedule-id"], pending.ScheduleId);

            await service.EnsureScheduledCoreAsync(entry.GrainId, entry.ReminderName, pending.ScheduleId, force: false, CancellationToken.None);
            var repaired = Assert.IsType<ReminderEntry>(await table.ReadRow(entry.GrainId, entry.ReminderName));
            Assert.NotEqual(pending.ScheduleId, repaired.ScheduleId);
            Assert.NotEmpty(repaired.JobId);
            response.SetResult(CreateDurableJob(requests[0]));
            var upserts = table.UpsertCount;
            await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, pending.ScheduleId, CancellationToken.None);
            Assert.Equal(upserts, table.UpsertCount);
            Assert.Empty(recipient.ReceivedStatuses);
            var afterLateJob = Assert.IsType<ReminderEntry>(await table.ReadRow(entry.GrainId, entry.ReminderName));
            Assert.Equal(repaired.ETag, afterLateJob.ETag);
            Assert.Equal(repaired.JobId, afterLateJob.JobId);

            await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, repaired.ScheduleId, CancellationToken.None);
            Assert.Single(recipient.ReceivedStatuses);
        }
        finally
        {
            response.TrySetCanceled(TestContext.Current.CancellationToken);
        }
    }
}
