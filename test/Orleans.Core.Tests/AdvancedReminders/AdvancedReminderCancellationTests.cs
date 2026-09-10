using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Xunit;
using ReminderData = Orleans.AdvancedReminders.ReminderData;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderCancellationTests : AdvancedReminderServiceTestBase
{
    [Theory]
    [InlineData("register", false)]
    [InlineData("register", true)]
    [InlineData("attribute", false)]
    [InlineData("attribute", true)]
    [InlineData("management", false)]
    [InlineData("management", true)]
    [InlineData("unregister", false)]
    [InlineData("unregister", true)]
    public async Task StalledCleanup_TimesOutAfterCommit_AndFencesOldOccurrence(string mutation, bool honorsCancellation)
    {
        var scenario = new CancellationScenario();
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken cleanupToken = default;
        scenario.Jobs.CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            cleanupToken = call.Arg<CancellationToken>();
            return honorsCancellation ? response.Task.WaitAsync(cleanupToken) : response.Task;
        });

        var operation = scenario.MutateAsync(mutation);
        try
        {
            Assert.True(cleanupToken.CanBeCanceled);
            Assert.False(operation.IsCompleted);
            await scenario.AssertCommittedAsync(mutation);

            scenario.Clock.Advance(TimeSpan.FromSeconds(4));
            Assert.False(cleanupToken.IsCancellationRequested);
            Assert.False(operation.IsCompleted);
            scenario.Clock.Advance(TimeSpan.FromSeconds(1));

            await operation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(cleanupToken.IsCancellationRequested);
            await scenario.AssertCommittedAsync(mutation);
        }
        finally
        {
            response.TrySetResult(true);
        }

        // Even a late backend reply must leave the replacement/removal intact.
        scenario.Clock.Advance(TimeSpan.FromHours(1));
        await scenario.AssertCommittedAsync(mutation);
    }

    public static IEnumerable<object[]> CleanupFailures()
    {
        foreach (var mutation in new[] { "register", "attribute", "management", "unregister" })
            foreach (var failure in new[] { "not-found", "synchronous", "asynchronous" })
            {
                yield return [mutation, failure];
            }
    }

    [Theory]
    [MemberData(nameof(CleanupFailures))]
    public async Task FailedCleanup_PreservesCommittedMutation_AndFencesOldOccurrence(string mutation, string failure)
    {
        var scenario = new CancellationScenario();
        scenario.Jobs.CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>()).Returns(_ => failure switch
        {
            "not-found" => Task.FromResult(false),
            "synchronous" => throw new InvalidOperationException("Injected synchronous cancellation failure."),
            _ => Task.FromException<bool>(new InvalidOperationException("Injected asynchronous cancellation failure.")),
        });

        await scenario.MutateAsync(mutation);

        scenario.Clock.Advance(TimeSpan.FromHours(1));
        await scenario.AssertCommittedAsync(mutation);
        _ = scenario.Jobs.Received(1).CancelAsync(
            Arg.Is<DurableJob>(job => job.Id == scenario.Previous.JobId && job.ShardId == scenario.Previous.JobShardId),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("register")]
    [InlineData("attribute")]
    [InlineData("management")]
    public async Task FailedDefinitionWrite_DoesNotCancelCurrentJob(string mutation)
    {
        var scenario = new CancellationScenario(failUpsertCall: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.MutateAsync(mutation));

        var retained = Assert.IsType<ReminderEntry>(await scenario.Table.ReadRow(scenario.Previous.GrainId, scenario.Previous.ReminderName));
        Assert.Equal(scenario.Previous.ScheduleId, retained.ScheduleId);
        Assert.Equal(scenario.Previous.JobId, retained.JobId);
        await scenario.Jobs.DidNotReceive().CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>());
        await scenario.Jobs.DidNotReceive().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
    }

    private sealed class CancellationScenario
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        public ILocalDurableJobManager Jobs { get; } = Substitute.For<ILocalDurableJobManager>();
        public ReminderEntry Previous { get; }
        public MutableReminderTable Table { get; }
        private readonly AdvancedReminderService _service;
        private readonly CallbackRemindable _recipient = new(() => Task.CompletedTask);

        public CancellationScenario(int failUpsertCall = 0)
        {
            Previous = CreateDueEntry(Clock.GetUtcNow(), "obsolete-job-cancellation");
            Previous.JobId = "previous-job";
            Previous.JobShardId = "previous-shard";
            Table = new MutableReminderTable(Previous) { FailUpsertCall = failUpsertCall };
            var factory = Substitute.For<IGrainFactory>();
            var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "cancellation-dispatcher"));
            factory.GetGrain<IAdvancedReminderDispatcherGrain>(Previous.GrainId.ToString(), null).Returns(dispatcher);
            factory.GetGrain<Orleans.AdvancedReminders.IRemindable>(Previous.GrainId).Returns(_recipient);
            Jobs.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(CreateDurableJob(call.Arg<ScheduleJobRequest>())));
            _service = CreateService(Table, jobManager: Jobs, grainFactory: factory, timeProvider: Clock);
            dispatcher.Service = _service;
        }

        public Task MutateAsync(string mutation)
        {
            var replacement = Clone(Previous, nextDueUtc: Clock.GetUtcNow().UtcDateTime.AddHours(1));
            return mutation switch
            {
                "register" => _service.RegisterOrUpdateCoreAsync(replacement, CancellationToken.None),
                "attribute" => _service.ReconcileAttributeCoreAsync(replacement, "changed-declaration", CancellationToken.None),
                "management" => _service.UpsertAndScheduleCoreAsync(replacement, CancellationToken.None),
                "unregister" => _service.UnregisterCoreAsync(Assert.IsType<ReminderData>(Previous.ToIGrainReminder()), CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };
        }

        public async Task AssertCommittedAsync(string mutation)
        {
            var current = await Table.ReadRow(Previous.GrainId, Previous.ReminderName);
            if (mutation == "unregister")
            {
                Assert.Null(current);
            }
            else
            {
                var replacement = Assert.IsType<ReminderEntry>(current);
                Assert.Equal(Previous.NextDueUtc!.Value.AddHours(1), replacement.NextDueUtc);
                Assert.NotEqual(Previous.ScheduleId, replacement.ScheduleId);
                Assert.NotEqual(Previous.JobId, replacement.JobId);
                Assert.NotEmpty(replacement.JobId);
            }

            await _service.ProcessDueReminderCoreAsync(Previous.GrainId, Previous.ReminderName, Previous.ScheduleId, CancellationToken.None);
            Assert.Empty(_recipient.ReceivedStatuses);
            var afterStaleDelivery = await Table.ReadRow(Previous.GrainId, Previous.ReminderName);
            Assert.Equal(current?.ScheduleId, afterStaleDelivery?.ScheduleId);
            Assert.Equal(current?.ETag, afterStaleDelivery?.ETag);
        }
    }
}
