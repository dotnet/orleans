using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Xunit;
using IRemindable = Orleans.AdvancedReminders.IRemindable;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderImmediateDeliveryTests : AdvancedReminderServiceTestBase
{
    [Theory]
    [InlineData(MissedReminderAction.Skip, 1, 0)]
    [InlineData(MissedReminderAction.Notify, 1, 0)]
    [InlineData(MissedReminderAction.FireImmediately, 1, 1)]
    [InlineData(MissedReminderAction.Skip, 2, 1)]
    [InlineData(MissedReminderAction.Notify, 2, 1)]
    [InlineData(MissedReminderAction.FireImmediately, 2, 1)]
    public async Task JobArrivingBeforeScheduleReturns_WaitsForRegistrationAndPreservesAttemptCount(
        MissedReminderAction action, int dequeueCount, int expectedCallbacks)
    {
        var now = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
        var id = GrainId.Create("test", "immediate-delivery");
        var entry = new ReminderEntry
        {
            GrainId = id,
            ReminderName = "once",
            StartAt = now.UtcDateTime.AddDays(-2),
            NextDueUtc = now.UtcDateTime.AddDays(-2),
            Period = TimeSpan.Zero,
            Action = action,
        };
        var table = new MutableReminderTable(null);
        var manager = Substitute.For<ILocalDurableJobManager>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var remindable = Substitute.For<IRemindable>();
        grainFactory.GetGrain<IRemindable>(id).Returns(remindable);
        var queuedDispatcher = Substitute.For<IAdvancedReminderDispatcherGrain, IGrainBase>();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(GrainId.Create("sys", "immediate-dispatcher"));
        ((IGrainBase)queuedDispatcher).GrainContext.Returns(context);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(id.ToString(), null).Returns(queuedDispatcher);
        var service = CreateService(table, jobManager: manager, grainFactory: grainFactory, timeProvider: new FakeTimeProvider(now));
        var handler = new AdvancedReminderDispatcherGrain(service);
        var registrationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? delivery = null;

        // Model the ordinary grain mailbox: this delivery cannot enter while registration owns it.
        queuedDispatcher.ProcessDueReminderAsync(id, "once", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => DeliverAfterRegistrationAsync(call.ArgAt<string>(2), call.ArgAt<int>(3)));
        manager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var job = CreateDurableJob(call.Arg<ScheduleJobRequest>());
            var jobContext = Substitute.For<IJobRunContext>();
            jobContext.Job.Returns(job);
            jobContext.DequeueCount.Returns(dequeueCount);
            delivery = handler.ExecuteJobAsync(jobContext, CancellationToken.None);
            Assert.False(delivery.IsCompleted, "Delivery bypassed the dispatcher mailbox before the job handle was persisted.");
            return Task.FromResult(job);
        });

        try
        {
            await service.RegisterOrUpdateCoreAsync(entry, CancellationToken.None);
            Assert.Equal(2, table.UpsertCount);
            var persisted = await table.ReadRow(id, "once");
            Assert.NotNull(persisted);
            Assert.NotEmpty(persisted.JobId);
            Assert.False(delivery!.IsCompleted);
        }
        finally
        {
            registrationFinished.TrySetResult();
        }

        await delivery!;
        Assert.Null(await table.ReadRow(id, "once"));
        await remindable.Received(expectedCallbacks).ReceiveReminder("once", Arg.Any<Orleans.AdvancedReminders.Runtime.TickStatus>());
        await queuedDispatcher.Received(1).ProcessDueReminderAsync(id, "once", entry.ScheduleId, dequeueCount, CancellationToken.None);

        async Task DeliverAfterRegistrationAsync(string scheduleId, int attempt)
        {
            await registrationFinished.Task;
            await service.ProcessDueReminderCoreAsync(id, "once", scheduleId, CancellationToken.None, attempt);
        }
    }
}
