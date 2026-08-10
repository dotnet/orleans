#nullable enable

using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Internal;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.TimerTests;
using UnitTests.GrainInterfaces;

namespace Tester.Cosmos.Reminders;

/// <summary>
/// Tests for Orleans reminders functionality using Azure Cosmos DB as the reminder service backing store.
/// </summary>
[TestCategory("Reminders"), TestCategory("Cosmos")]
public class ReminderTests_Cosmos : ReminderTestsBase, IClassFixture<ReminderTests_Cosmos.Fixture>
{
    public class Fixture : BaseInProcessTestClusterFixture
    {
        private ReminderTestClock? _reminderClock;
        internal ReminderTestClock ReminderClock => _reminderClock ?? throw new InvalidOperationException($"{nameof(ReminderTestClock)} has not been configured.");

        protected override void CheckPreconditionsOrThrow() => CosmosTestUtils.CheckCosmosStorage();

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            _reminderClock = builder.AddReminderTestClock();
            builder.ConfigureSilo((_, siloBuilder) =>
            {
                siloBuilder.UseCosmosReminderService(options =>
                {
                    options.ConfigureTestDefaults();
                });
            });
        }

        public override async Task DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                _reminderClock?.Dispose();
            }
        }
    }

    public ReminderTests_Cosmos(Fixture fixture) : base(fixture.ReminderClock, fixture.HostedCluster)
    {
        fixture.EnsurePreconditionsMet();
    }

    // Basic tests

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_Basic_StopByRef()
    {
        await Test_Reminders_Basic_StopByRef();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Cosmos_UpdateReminder_DoesNotRestartLocalReminder()
    {
        await Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_Basic_ListOps()
    {
        await Test_Reminders_Basic_ListOps();
    }

    // Single join tests ... multi grain, multi reminders

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_1J_MultiGrainMultiReminders()
    {
        await Test_Reminders_1J_MultiGrainMultiReminders();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_ReminderNotFound()
    {
        await Test_Reminders_ReminderNotFound();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_Basic()
    {
        // start up a test grain and get the period that it's programmed to use.
        IReminderTestGrain2 grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        TimeSpan period = await grain.GetReminderPeriod(DR);
        // start up the 'DR' reminder and wait for two ticks to pass.
        await grain.StartReminder(DR);
        long last = await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 2);
        Assert.Equal(2, last);
        // stop the timer and wait for a whole period.
        await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder);
        await AdvanceReminderTimeAsync(period);
        long curr = await grain.GetCounter(DR);
        Assert.Equal(last, curr);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_Basic_Restart()
    {
        IReminderTestGrain2 grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        TimeSpan period = await grain.GetReminderPeriod(DR);
        await grain.StartReminder(DR);
        long last = await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 2);
        Assert.Equal(2, last);

        await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder);
        TimeSpan sleepFor = period;
        await AdvanceReminderTimeAsync(sleepFor);
        long curr = await grain.GetCounter(DR);
        Assert.Equal(last, curr);
        AssertIsInRange(curr, last, last + 1, grain, DR, sleepFor);

        // start the same reminder again
        await grain.StartReminder(DR);
        sleepFor = period.Multiply(2);
        curr = await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 2);
        AssertIsInRange(curr, 2, 3, grain, DR, sleepFor);
        await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder); // cleanup
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_MultipleReminders()
    {
        IReminderTestGrain2 grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        await PerGrainMultiReminderTest(grain);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_2J_MultiGrainMultiReminders()
    {
        IReminderTestGrain2 g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g3 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g4 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g5 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        using var cts = new CancellationTokenSource(CHURN_ENDWAIT);

        await Test_Reminders_MultiGrainMultiReminders(
            async cancellationToken =>
            {
                await using (await PauseReminderTimeAsync(cancellationToken))
                {
                    log.LogInformation("Starting 2 extra silos");
                    await StartAdditionalSilosAndWaitForReminderServicesAsync(
                        2,
                        cancellationToken,
                        startAdditionalSiloOnNewPort: true);
                }
            },
            cts.Token,
            g1,
            g2,
            g3,
            g4,
            g5);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_MultiGrainMultiReminders()
    {
        IReminderTestGrain2 g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g3 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g4 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g5 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        using var cts = new CancellationTokenSource(ENDWAIT);

        await Test_Reminders_MultiGrainMultiReminders(
            afterFirstTick: null,
            cts.Token,
            g1,
            g2,
            g3,
            g4,
            g5);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_1F_Basic()
    {
        IReminderTestGrain2 g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        using var cts = new CancellationTokenSource(ENDWAIT);

        await PrepareForGrainFailureAsync(cts.Token, g1);

        // stop the secondary silo
        await using (await PauseReminderTimeAsync(cts.Token))
        {
            var reminderOwner = GetReminderOwner(g1, DR);
            log.LogInformation("Stopping reminder owner {SiloAddress}", reminderOwner.SiloAddress);
            await StopSiloAsync(reminderOwner);
            await WaitForLivenessToStabilizeAsync().WaitAsync(cts.Token);
        }

        await CompleteGrainFailureTestAsync(cts.Token, g1);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_2F_MultiGrain()
    {
        using var cts = new CancellationTokenSource(ENDWAIT);
        _ = await StartAdditionalSilosAndWaitForReminderServicesAsync(
            2,
            cts.Token,
            startAdditionalSiloOnNewPort: true);

        IReminderTestGrain2 g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g3 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g4 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g5 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        IAddressable[] grains = [g1, g2, g3, g4, g5];
        await PrepareForGrainFailureAsync(cts.Token, grains);

        // stop a couple of silos
        await using (await PauseReminderTimeAsync(cts.Token))
        {
            log.LogInformation("Stopping 2 silos");
            var reminderOwner = GetReminderOwner(g1, DR);
            var otherSilo = HostedCluster.GetActiveSilos().First(silo => !silo.SiloAddress.Equals(reminderOwner.SiloAddress));
            await StopSiloAsync(reminderOwner);
            await StopSiloAsync(otherSilo);
            await WaitForLivenessToStabilizeAsync().WaitAsync(cts.Token);
        }

        await CompleteGrainFailureTestAsync(cts.Token, grains);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_1F1J_MultiGrain()
    {
        using var cts = new CancellationTokenSource(ENDWAIT);
        _ = await StartAdditionalSilosAndWaitForReminderServicesAsync(1, cts.Token);

        IReminderTestGrain2 g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g3 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g4 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g5 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        IAddressable[] grains = [g1, g2, g3, g4, g5];
        await PrepareForGrainFailureAsync(cts.Token, grains);

        var siloToKill = GetReminderOwner(g1, DR);
        // stop a silo and join a new one in parallel
        await using (await PauseReminderTimeAsync(cts.Token))
        {
            log.LogInformation("Stopping a silo and joining a silo");
            await StopSiloAndStartAdditionalSiloAsync(
                siloToKill,
                cts.Token,
                startAdditionalSiloOnNewPort: true);
        }

        await CompleteGrainFailureTestAsync(cts.Token, grains);
        log.LogInformation("\n\n\nReminderTest_1F1J_MultiGrain passed OK.\n\n\n");
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_RegisterSameReminderTwice()
    {
        IReminderTestGrain2 grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        Task<IGrainReminder> promise1 = grain.StartReminder(DR);
        Task<IGrainReminder> promise2 = grain.StartReminder(DR);
        Task<IGrainReminder>[] tasks = { promise1, promise2 };
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
        //Assert.NotEqual(promise1.Result, promise2.Result);
        // TODO: write tests where period of a reminder is changed
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_GT_Basic()
    {
        IReminderTestGrain2 g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestCopyGrain g2 = GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
        TimeSpan period = await g1.GetReminderPeriod(DR); // using same period
        using var cts = new CancellationTokenSource(ENDWAIT);

        var reminder1 = await g1.StartReminder(DR);
        Assert.Equal(DR, reminder1.ReminderName);
        await observer.WaitForActiveReminderCountAsync(g1, 1, cts.Token, DR);
        await AdvanceReminderTimeAndWaitForTickAsync(g1, DR, period, cts.Token);

        var reminder2 = await g2.StartReminder(DR);
        Assert.Equal(DR, reminder2.ReminderName);
        await observer.WaitForActiveReminderCountAsync(g2, 1, cts.Token, DR);

        await AdvanceReminderTimeAsync(TimeSpan.Zero, cts.Token);
        var g1TickTask = observer.WaitForAdditionalTickCountAsync(g1, 1, cts.Token, DR);
        var g2TickTask = observer.WaitForAdditionalTickCountAsync(g2, 1, cts.Token, DR);
        await AdvanceReminderTimeAsync(period, cts.Token);
        await Task.WhenAll(g1TickTask, g2TickTask);

        await StopReminderAndWaitForQuiescenceAsync(g1, DR, g1.StopReminder, cts.Token);
        Assert.Null(await g1.GetReminderObject(DR));
        Assert.Equal(1, observer.GetActiveReminderCount(g2.GetGrainId(), DR));

        var stopped1TickCount = observer.GetTickCount(g1.GetGrainId(), DR);
        await AdvanceReminderTimeAndWaitForTickAsync(g2, DR, period, cts.Token);
        Assert.Equal(stopped1TickCount, observer.GetTickCount(g1.GetGrainId(), DR));

        await StopReminderAndWaitForQuiescenceAsync(g2, DR, g2.StopReminder, cts.Token);
        var stopped2TickCount = observer.GetTickCount(g2.GetGrainId(), DR);
        await AdvanceReminderTimeAsync(period, cts.Token);
        Assert.Equal(stopped2TickCount, observer.GetTickCount(g2.GetGrainId(), DR));
    }

    private async Task AdvanceReminderTimeAndWaitForTickAsync(IAddressable grain, string reminderName, TimeSpan amount, CancellationToken cancellationToken)
    {
        await AdvanceReminderTimeAsync(TimeSpan.Zero, cancellationToken);
        var tickTask = observer.WaitForAdditionalTickCountAsync(grain, 1, cancellationToken, reminderName);
        await AdvanceReminderTimeAsync(amount, cancellationToken);
        await tickTask;
    }

    [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/4319"), TestCategory("Functional")]
    public async Task Rem_Azure_GT_1F1J_MultiGrain()
    {
        using var cts = new CancellationTokenSource(ENDWAIT);
        _ = await StartAdditionalSilosAndWaitForReminderServicesAsync(1, cts.Token);

        IReminderTestGrain2 g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestCopyGrain g3 = GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
        IReminderTestCopyGrain g4 = GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());

        IAddressable[] grains = [g1, g2, g3, g4];
        await PrepareForGrainFailureAsync(cts.Token, grains);

        var siloToKill = GetReminderOwner(g1, DR);
        // stop a silo and join a new one in parallel
        await using (await PauseReminderTimeAsync(cts.Token))
        {
            log.LogInformation("Stopping a silo and joining a silo");
            await StopSiloAndStartAdditionalSiloAsync(siloToKill, cts.Token);
        }

        await CompleteGrainFailureTestAsync(cts.Token, grains);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_Wrong_LowerThanAllowedPeriod()
    {
        IReminderTestGrain2 grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.StartReminder(DR, TimeSpan.FromMilliseconds(3000), true));
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Rem_Azure_Wrong_Grain()
    {
        IReminderGrainWrong grain = GrainFactory.GetGrain<IReminderGrainWrong>(0);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.StartReminder(DR));
    }
}
