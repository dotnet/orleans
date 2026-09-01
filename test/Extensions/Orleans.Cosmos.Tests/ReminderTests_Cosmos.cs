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

[TestSuite("Functional")]
[TestProvider("Cosmos")]
[TestArea("Reminders")]
[TestCategory("Reminders"), TestCategory("Cosmos")]
public sealed class ReminderServiceLifecycleTests_Cosmos
    : ReminderServiceLifecycleTestsBase, IClassFixture<ReminderTests_Cosmos.Fixture>
{
    public ReminderServiceLifecycleTests_Cosmos(ReminderTests_Cosmos.Fixture fixture)
        : base(fixture.ReminderClock, fixture.HostedCluster, "Cosmos")
    {
        fixture.EnsurePreconditionsMet();
    }
}

/// <summary>
/// Tests for Orleans reminders functionality using Azure Cosmos DB as the reminder service backing store.
/// </summary>
[TestProvider("Cosmos")]
[TestArea("Reminders")]
[TestCategory("Reminders"), TestCategory("Cosmos")]
public class ReminderTests_Cosmos : ReminderTestsBase, IClassFixture<ReminderTests_Cosmos.Fixture>
{
    public class Fixture : BaseInProcessTestClusterFixture
    {
        private static readonly TimeSpan ReminderServiceStartupTimeout = TimeSpan.FromMinutes(5);
        private ReminderTestClock? _reminderClock;
        private ReminderDiagnosticObserver? _startupObserver;
        internal ReminderTestClock ReminderClock
        {
            get
            {
                EnsurePreconditionsMet();
                return _reminderClock ?? throw new InvalidOperationException($"{nameof(ReminderTestClock)} has not been configured.");
            }
        }

        protected override void CheckPreconditionsOrThrow() => CosmosTestUtils.CheckCosmosStorage();

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            _startupObserver = ReminderDiagnosticObserver.Create(builder);
            _reminderClock = builder.AddReminderTestClock();
            builder.ConfigureSilo((_, siloBuilder) =>
            {
                siloBuilder.UseCosmosReminderService(options =>
                {
                    options.ConfigureTestDefaults();
                });
            });
        }

        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }

            var silos = HostedCluster.Silos.ToArray();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            cancellation.CancelAfter(ReminderServiceStartupTimeout);
            var startedTasks = silos
                .Select(silo => _startupObserver!.WaitForReminderServiceStartedAsync(cancellation.Token, silo.SiloAddress))
                .ToArray();

            try
            {
                await Task.WhenAll(startedTasks);
            }
            catch (OperationCanceledException) when (
                cancellation.IsCancellationRequested
                && !TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                var missing = silos
                    .Where((_, index) => !startedTasks[index].IsCompletedSuccessfully)
                    .Select(silo => silo.SiloAddress);
                throw new TimeoutException(
                    $"Cosmos reminder services did not start within {ReminderServiceStartupTimeout}. Missing silos: {string.Join(", ", missing)}.");
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                _reminderClock?.Dispose();
                _startupObserver?.Dispose();
            }
        }
    }

    public ReminderTests_Cosmos(Fixture fixture) : base(fixture.ReminderClock, fixture.HostedCluster)
    {
        fixture.EnsurePreconditionsMet();
    }

    // Basic tests

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_Basic_StopByRef()
    {
        await Test_Reminders_Basic_StopByRef(TestContext.Current.CancellationToken);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Cosmos_UpdateReminder_DoesNotRestartLocalReminder()
    {
        await Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder(TestContext.Current.CancellationToken);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_Basic_ListOps()
    {
        await Test_Reminders_Basic_ListOps(TestContext.Current.CancellationToken);
    }

    // Single join tests ... multi grain, multi reminders

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_1J_MultiGrainMultiReminders()
    {
        await Test_Reminders_1J_MultiGrainMultiReminders(TestContext.Current.CancellationToken);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_ReminderNotFound()
    {
        await Test_Reminders_ReminderNotFound(TestContext.Current.CancellationToken);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_Basic()
    {
        // start up a test grain and get the period that it's programmed to use.
        IReminderTestGrain2 grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        TimeSpan period = await grain.GetReminderPeriod(DR);
        // start up the 'DR' reminder and wait for two ticks to pass.
        await grain.StartReminder(DR);
        long last = await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, last);
        // stop the timer and wait for a whole period.
        await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, TestContext.Current.CancellationToken);
        await AdvanceReminderTimeAsync(period, TestContext.Current.CancellationToken);
        long curr = await grain.GetCounter(DR);
        Assert.Equal(last, curr);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_Basic_Restart()
    {
        IReminderTestGrain2 grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        TimeSpan period = await grain.GetReminderPeriod(DR);
        await grain.StartReminder(DR);
        long last = await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, last);

        await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, TestContext.Current.CancellationToken);
        TimeSpan sleepFor = period;
        await AdvanceReminderTimeAsync(sleepFor, TestContext.Current.CancellationToken);
        long curr = await grain.GetCounter(DR);
        Assert.Equal(last, curr);
        AssertIsInRange(curr, last, last + 1, grain, DR, sleepFor);

        // start the same reminder again
        await grain.StartReminder(DR);
        sleepFor = period.Multiply(2);
        curr = await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 2, TestContext.Current.CancellationToken);
        AssertIsInRange(curr, 2, 3, grain, DR, sleepFor);
        await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, TestContext.Current.CancellationToken); // cleanup
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_MultipleReminders()
    {
        IReminderTestGrain2 grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        await PerGrainMultiReminderTest(grain, TestContext.Current.CancellationToken);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_2J_MultiGrainMultiReminders()
    {
        await Test_Reminders_2J_MultiGrainMultiReminders(TestContext.Current.CancellationToken);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_MultiGrainMultiReminders()
    {
        IReminderTestGrain2 g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g3 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g4 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g5 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(ENDWAIT);

        await Test_Reminders_MultiGrainMultiReminders(
            afterFirstTick: null,
            cts.Token,
            g1,
            g2,
            g3,
            g4,
            g5);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_1F_Basic()
    {
        IReminderTestGrain2 g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(ENDWAIT);

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

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_2F_MultiGrain()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(ENDWAIT);
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

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_1F1J_MultiGrain()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(ENDWAIT);
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

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_RegisterSameReminderTwice()
    {
        IReminderTestGrain2 grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        Task<IGrainReminder> promise1 = grain.StartReminder(DR);
        Task<IGrainReminder> promise2 = grain.StartReminder(DR);
        Task<IGrainReminder>[] tasks = { promise1, promise2 };
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        //Assert.NotEqual(promise1.Result, promise2.Result);
        // TODO: write tests where period of a reminder is changed
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_GT_Basic()
    {
        IReminderTestGrain2 g1 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestCopyGrain g2 = GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
        TimeSpan period = await g1.GetReminderPeriod(DR); // using same period
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(ENDWAIT);

        var reminder1 = await g1.StartReminder(DR);
        Assert.Equal(DR, reminder1.ReminderName);
        await AdvanceRemindersByTicksAsync(1, cts.Token, (g1, DR));

        var reminder2 = await g2.StartReminder(DR);
        Assert.Equal(DR, reminder2.ReminderName);
        await AdvanceRemindersByTicksAsync(1, cts.Token, (g1, DR), (g2, DR));

        await StopReminderAndWaitForQuiescenceAsync(g1, DR, g1.StopReminder, cts.Token);
        Assert.Null(await g1.GetReminderObject(DR));
        Assert.Equal(1, observer.GetActiveReminderCount(g2.GetGrainId(), DR));

        var stopped1TickCount = observer.GetTickCount(g1.GetGrainId(), DR);
        await AdvanceRemindersByTicksAsync(1, cts.Token, (g2, DR));
        Assert.Equal(stopped1TickCount, observer.GetTickCount(g1.GetGrainId(), DR));

        await StopReminderAndWaitForQuiescenceAsync(g2, DR, g2.StopReminder, cts.Token);
        var stopped2TickCount = observer.GetTickCount(g2.GetGrainId(), DR);
        await AdvanceReminderTimeAsync(period, cts.Token);
        Assert.Equal(stopped2TickCount, observer.GetTickCount(g2.GetGrainId(), DR));
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_GT_1F1J_MultiGrain()
    {
        await Test_Reminders_GT_1F1J_MultiGrain(TestContext.Current.CancellationToken);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_Wrong_LowerThanAllowedPeriod()
    {
        IReminderTestGrain2 grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.StartReminder(DR, TimeSpan.FromMilliseconds(3000), true));
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task Rem_Azure_Wrong_Grain()
    {
        IReminderGrainWrong grain = GrainFactory.GetGrain<IReminderGrainWrong>(0);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.StartReminder(DR));
    }
}
