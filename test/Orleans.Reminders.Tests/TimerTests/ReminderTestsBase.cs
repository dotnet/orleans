#nullable enable

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.Grains;
using UnitTests.GrainInterfaces;
using Xunit;
using ReminderEvents = Orleans.Reminders.Diagnostics.ReminderEvents;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests;

/// <summary>
/// Base class for reminder tests providing common test operations and utilities.
/// Uses <see cref="ReminderDiagnosticObserver"/> for event-driven waiting instead of Task.Delay.
/// </summary>
public class ReminderTestsBase : OrleansTestingBase, IAsyncDisposable
{
    protected InProcessTestCluster HostedCluster { get; }
    protected static readonly TimeSpan LEEWAY = TimeSpan.FromMilliseconds(500);
    protected static readonly TimeSpan ENDWAIT = TimeSpan.FromMinutes(2);
    protected static readonly TimeSpan CHURN_ENDWAIT = TimeSpan.FromMinutes(5);

    protected const string DR = "DEFAULT_REMINDER";
    protected const string R1 = "REMINDER_1";
    protected const string R2 = "REMINDER_2";

    protected const long retries = 3;

    protected const long failAfter = 2;
    protected const long failCheckAfter = 6;
    protected ILogger log;
    protected readonly ReminderLifecycleHarness observer;
    private ReminderTestClock ReminderClock { get; }

    public ReminderTestsBase(ReminderTestClock reminderClock, InProcessTestCluster hostedCluster)
    {
        ArgumentNullException.ThrowIfNull(reminderClock);
        ArgumentNullException.ThrowIfNull(hostedCluster);

        var grainFactory = hostedCluster.Client;
        if (grainFactory is null)
        {
            throw new InvalidOperationException($"{nameof(InProcessTestCluster)} client is not initialized.");
        }

        HostedCluster = hostedCluster;
        GrainFactory = grainFactory;
        ReminderClock = reminderClock;

        var filters = new LoggerFilterOptions();
#if DEBUG
        filters.AddFilter("Storage", LogLevel.Trace);
        filters.AddFilter("Reminder", LogLevel.Trace);
#endif

        log = TestingUtils.CreateDefaultLoggerFactory(TestingUtils.CreateTraceFileName("client", DateTime.Now.ToString("yyyyMMdd_hhmmss")), filters).CreateLogger<ReminderTestsBase>();
        observer = new ReminderLifecycleHarness(hostedCluster);
    }

    public IGrainFactory GrainFactory { get; }

    protected DateTimeOffset ReminderUtcNow => ReminderClock.UtcNow;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ExecuteCleanupAsync(
                ClearReminderTableAsync,
                observer.RefreshActiveServicesAsync,
                observer.WaitForGlobalQuiescenceAsync,
                TestConstants.InitTimeout);
        }
        finally
        {
            observer.Dispose();
        }
    }

    protected Task ClearReminderTableAsync(CancellationToken cancellationToken)
    {
        // ReminderTable.Clear() cannot be called from a non-Orleans thread,
        // so proxy the call through a grain.
        var controlProxy = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        return controlProxy.EraseReminderTable().WaitAsync(cancellationToken);
    }

    internal static async Task ExecuteCleanupAsync(
        Func<CancellationToken, Task> clearReminderTable,
        Func<CancellationToken, Task> refreshReminderServices,
        Func<CancellationToken, Task> waitForGlobalQuiescence,
        TimeSpan timeout)
    {
        using var cleanupCancellation = new CancellationTokenSource(timeout);
        List<Exception>? exceptions = null;
        if (await RunPhase(clearReminderTable)
            && await RunPhase(refreshReminderServices))
        {
            await RunPhase(waitForGlobalQuiescence);
        }

        if (exceptions is [var exception])
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        if (exceptions is { Count: > 1 })
        {
            throw new AggregateException("Reminder test cleanup failed.", exceptions);
        }

        async Task<bool> RunPhase(Func<CancellationToken, Task> phase)
        {
            try
            {
                await phase(cleanupCancellation.Token);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }

            return !cleanupCancellation.IsCancellationRequested;
        }
    }

    public Task Test_Reminders_Basic_StopByRef()
        => Test_Reminders_Basic_StopByRef(TestContext.Current.CancellationToken);

    public async Task Test_Reminders_Basic_StopByRef(CancellationToken cancellationToken)
    {
        IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        IGrainReminder r1 = await grain.StartReminder(DR).WaitAsync(cancellationToken);
        IGrainReminder r2 = await grain.StartReminder(DR).WaitAsync(cancellationToken);
        try
        {
            // First handle should now be out of date once the second handle to the same reminder was obtained
            await grain.StopReminder(r1).WaitAsync(cancellationToken);
            Assert.Fail("Removed reminder1, which shouldn't be possible.");
        }
        catch (Exception exc)
        {
            log.LogInformation(exc, "Couldn't remove {Reminder}, as expected.", r1);
        }

        await grain.StopReminder(r2).WaitAsync(cancellationToken);
        log.LogInformation("Removed reminder2 successfully");

        // trying to see if readreminder works
        _ = await grain.StartReminder(DR).WaitAsync(cancellationToken);
        _ = await grain.StartReminder(DR).WaitAsync(cancellationToken);
        _ = await grain.StartReminder(DR).WaitAsync(cancellationToken);
        _ = await grain.StartReminder(DR).WaitAsync(cancellationToken);

        IGrainReminder? r = await grain.GetReminderObject(DR).WaitAsync(cancellationToken);
        await grain.StopReminder(r!).WaitAsync(cancellationToken);
        log.LogInformation("Removed got reminder successfully");
    }

    public Task Test_Reminders_Basic_ListOps()
        => Test_Reminders_Basic_ListOps(TestContext.Current.CancellationToken);

    public async Task Test_Reminders_Basic_ListOps(CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        log.LogInformation("Start Grain Id = {GrainId}", id);
        IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(id);
        const int count = 5;
        Task<IGrainReminder>[] startReminderTasks = new Task<IGrainReminder>[count];
        for (int i = 0; i < count; i++)
        {
            startReminderTasks[i] = grain.StartReminder(DR + "_" + i);
            log.LogInformation("Started {ReminderName}_{ReminderNumber}", DR, i);
        }

        await Task.WhenAll(startReminderTasks).WaitAsync(cancellationToken);
        // do comparison on strings
        List<string> registered = (from reminder in startReminderTasks select reminder.Result.ReminderName).ToList();

        log.LogInformation("Waited");

        List<IGrainReminder> remindersList = await grain.GetRemindersList().WaitAsync(cancellationToken);
        List<string> fetched = (from reminder in remindersList select reminder.ReminderName).ToList();

        foreach (var remRegistered in registered)
        {
            Assert.True(fetched.Remove(remRegistered), $"Couldn't get reminder {remRegistered}. " +
                                                       $"Registered list: {Utils.EnumerableToString(registered)}, " +
                                                       $"fetched list: {Utils.EnumerableToString(remindersList, r => r.ReminderName)}");
        }
        Assert.True(fetched.Count == 0, $"More than registered reminders. Extra: {Utils.EnumerableToString(fetched)}");

        // Wait for each reminder to tick twice using the observer
        log.LogInformation("Time tests");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ENDWAIT);
        var reminderNames = Enumerable.Range(0, count).Select(i => DR + "_" + i).ToArray();
        await AdvanceRemindersByTicksAsync(2, cts.Token, GetReminderIdentities([grain], reminderNames));

        // Verify via grain counters
        foreach (var reminderName in reminderNames)
        {
            Assert.Equal(2, await grain.GetCounter(reminderName).WaitAsync(cts.Token));
        }
    }

    public Task Test_Reminders_1J_MultiGrainMultiReminders()
        => Test_Reminders_1J_MultiGrainMultiReminders(TestContext.Current.CancellationToken);

    public async Task Test_Reminders_1J_MultiGrainMultiReminders(CancellationToken cancellationToken)
    {
        IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var initialSilos = HostedCluster.GetActiveSilos().ToHashSet();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CHURN_ENDWAIT);
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        Task<List<InProcessSiloHandle>>? startSilosTask = null;
        try
        {
            await Test_Reminders_MultiGrainMultiReminders(
                async cancellationToken =>
                {
                    await using (await PauseReminderTimeAsync(cancellationToken))
                    {
                        log.LogInformation("Starting another silo");
                        startSilosTask = StartAdditionalSilosAsync(
                            1,
                            startupCancellation.Token,
                            startAdditionalSiloOnNewPort: true);
                        var additionalSilos = await WaitForAdditionalSilosAndReminderServicesAsync(startSilosTask, cancellationToken);
                        _ = Assert.Single(additionalSilos);
                    }
                },
                cts.Token,
                g1,
                g2,
                g3,
                g4,
                g5);
        }
        finally
        {
            await CleanupAdditionalSilosAsync(initialSilos, startupCancellation, startSilosTask);
        }
    }

    public Task Test_Reminders_2J_MultiGrainMultiReminders()
        => Test_Reminders_2J_MultiGrainMultiReminders(TestContext.Current.CancellationToken);

    public async Task Test_Reminders_2J_MultiGrainMultiReminders(CancellationToken cancellationToken)
    {
        IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var initialSilos = HostedCluster.GetActiveSilos().ToHashSet();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CHURN_ENDWAIT);
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        Task<List<InProcessSiloHandle>>? startSilosTask = null;
        try
        {
            await Test_Reminders_MultiGrainMultiReminders(
                async cancellationToken =>
                {
                    await using (await PauseReminderTimeAsync(cancellationToken))
                    {
                        log.LogInformation("Starting 2 extra silos");
                        startSilosTask = StartAdditionalSilosAsync(
                            2,
                            startupCancellation.Token,
                            startAdditionalSiloOnNewPort: true);
                        await WaitForAdditionalSilosAndReminderServicesAsync(startSilosTask, cancellationToken);
                    }
                },
                cts.Token,
                g1,
                g2,
                g3,
                g4,
                g5);
        }
        finally
        {
            await CleanupAdditionalSilosAsync(initialSilos, startupCancellation, startSilosTask);
        }
    }

    public Task Test_Reminders_ReminderNotFound()
        => Test_Reminders_ReminderNotFound(TestContext.Current.CancellationToken);

    public async Task Test_Reminders_ReminderNotFound(CancellationToken cancellationToken)
    {
        IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        // request a reminder that does not exist
        IGrainReminder? reminder = await g1.GetReminderObject("blarg").WaitAsync(cancellationToken);
        Assert.Null(reminder);
    }

    public Task Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder()
        => Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder(TestContext.Current.CancellationToken);

    public async Task Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ENDWAIT);

        var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var grainId = grain.GetGrainId();

        await grain.StartReminder(DR).WaitAsync(cts.Token);
        var firstTickCount = await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 1, cts.Token);
        Assert.Equal(1, observer.GetActiveReminderCount(grainId, DR));

        using (var recorder = new ReminderEventRecorder(ReminderEvents.AllEvents))
        {
            await grain.StartReminder(DR).WaitAsync(cts.Token);
            await SynchronizeReminderSchedulesAsync(cts.Token, (grain, DR));
            await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), firstTickCount + 1, cts.Token);

            Assert.Contains(recorder.Events, evt => evt is ReminderEvents.Registered registered && registered.GrainId == grainId && registered.ReminderName == DR);
            Assert.Contains(recorder.Events, evt => evt is ReminderEvents.LocalReminderScheduleChanged { GrainId: var eventGrainId, ReminderName: DR } && eventGrainId == grainId);
            Assert.Contains(recorder.Events, evt => evt is ReminderEvents.LocalReminderTickWaitArmed { GrainId: var eventGrainId, ReminderName: DR } && eventGrainId == grainId);
            Assert.DoesNotContain(recorder.Events, evt => evt is ReminderEvents.LocalReminderStarted { GrainId: var eventGrainId, ReminderName: DR } && eventGrainId == grainId);
            Assert.DoesNotContain(recorder.Events, evt => evt is ReminderEvents.LocalReminderStopped { GrainId: var eventGrainId, ReminderName: DR } && eventGrainId == grainId);
        }

        Assert.Equal(1, observer.GetActiveReminderCount(grainId, DR));
        await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, cts.Token);
        Assert.Equal(0, observer.GetActiveReminderCount(grainId, DR));
    }

    public async Task Test_Reminders_GT_1F1J_MultiGrain(CancellationToken cancellationToken)
    {
        var initialSilos = HostedCluster.GetActiveSilos().ToHashSet();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ENDWAIT);
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        Task<List<InProcessSiloHandle>>? setupJoinTask = null;
        Task<List<InProcessSiloHandle>>? failoverJoinTask = null;

        try
        {
            setupJoinTask = StartAdditionalSilosAsync(1, startupCancellation.Token);
            var failedSilo = Assert.Single(
                await WaitForAdditionalSilosAndReminderServicesAsync(setupJoinTask, cts.Token));

            var g1 = await GetGrainOwnedBySiloAsync<IReminderTestGrain2>(failedSilo, cts.Token);
            var g2 = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var g3 = GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
            var g4 = GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
            IAddressable[] grains = [g1, g2, g3, g4];
            var reminders = GetReminderIdentities(grains, DR);

            await PrepareForGrainFailureAsync(cts.Token, grains);
            await AssertReminderOwnershipAndSchedulesAsync(reminders, failAfter, cts.Token);
            Assert.Equal(failedSilo.SiloAddress, GetReminderOwner(g1, DR).SiloAddress);

            await using (await PauseReminderTimeAsync(cts.Token))
            {
                log.LogInformation(
                    "Stopping reminder owner {SiloAddress} while joining a replacement silo",
                    failedSilo.SiloAddress);
                var stopTask = StopSiloAsync(failedSilo);
                failoverJoinTask = StartAdditionalSilosAsync(1, startupCancellation.Token);
                await Task.WhenAll(stopTask, failoverJoinTask).WaitAsync(cts.Token);
                _ = Assert.Single(
                    await WaitForReminderServicesStartedAsync(failoverJoinTask, cts.Token));
                await InvokeGrainCallsAfterTopologyConvergenceAsync(
                    cts.Token,
                    [.. grains.Select(grain => new Func<Task>(async () =>
                    {
                        _ = await GetReminderPeriodAsync(grain, DR).WaitAsync(cts.Token);
                    }))]);
                await AssertReminderOwnershipAndSchedulesAsync(reminders, failAfter, cts.Token);
                await AssertReminderCountersAsync(grains, cts.Token, (DR, failAfter));
                Assert.DoesNotContain(
                    failedSilo.SiloAddress,
                    reminders.SelectMany(reminder =>
                        observer.GetActiveReminderSilos(reminder.Grain.GetGrainId(), reminder.ReminderName)));
            }

            await CompleteGrainFailureTestWithoutReachabilityRetriesAsync(cts.Token, grains);
            AssertRemindersStopped(reminders, failCheckAfter);
        }
        finally
        {
            await CleanupAdditionalSilosAsync(
                initialSilos,
                startupCancellation,
                setupJoinTask,
                failoverJoinTask);
        }
    }

    protected Task<List<InProcessSiloHandle>> StartAdditionalSilosAsync(
        int silosToStart,
        CancellationToken cancellationToken,
        bool startAdditionalSiloOnNewPort = false)
    {
        return HostedCluster.StartSilosAsync(silosToStart, cancellationToken);
    }

    protected Task WaitForLivenessToStabilizeAsync(bool didKill = false)
    {
        return HostedCluster.WaitForLivenessToStabilizeAsync(didKill);
    }

    protected async Task<List<InProcessSiloHandle>> StartAdditionalSilosAndWaitForReminderServicesAsync(
        int silosToStart,
        CancellationToken cancellationToken,
        bool startAdditionalSiloOnNewPort = false)
    {
        var startSilosTask = StartAdditionalSilosAsync(
            silosToStart,
            cancellationToken,
            startAdditionalSiloOnNewPort);
        return await WaitForAdditionalSilosAndReminderServicesAsync(startSilosTask, cancellationToken);
    }

    private async Task<List<InProcessSiloHandle>> WaitForAdditionalSilosAndReminderServicesAsync(
        Task<List<InProcessSiloHandle>> startSilosTask,
        CancellationToken cancellationToken)
    {
        var result = await WaitForReminderServicesStartedAsync(startSilosTask, cancellationToken);
        await WaitForTopologyConvergenceAsync(cancellationToken);
        return result;
    }

    private async Task<List<InProcessSiloHandle>> WaitForReminderServicesStartedAsync(
        Task<List<InProcessSiloHandle>> startSilosTask,
        CancellationToken cancellationToken)
    {
        var result = await startSilosTask.WaitAsync(cancellationToken);
        await observer.WaitForServicesReadyAsync(
            result,
            CHURN_ENDWAIT,
            cancellationToken);
        return result;
    }

    private async Task CleanupAdditionalSilosAsync(
        HashSet<InProcessSiloHandle> initialSilos,
        CancellationTokenSource startupCancellation,
        params Task<List<InProcessSiloHandle>>?[] startSilosTasks)
    {
        if (startSilosTasks.All(static task => task is null))
        {
            return;
        }

        using var cleanupCts = new CancellationTokenSource(CHURN_ENDWAIT);
        await ReminderLifecycleHarness.CleanupPartialStartupAsync(
            initialSilos,
            Task.WhenAll(startSilosTasks.OfType<Task<List<InProcessSiloHandle>>>()),
            () => HostedCluster.GetActiveSilos().ToArray(),
            StopSiloAsync,
            () => observer.WaitForTopologyReconciledAsync(
                WaitForLivenessToStabilizeAsync(didKill: true),
                [],
                CHURN_ENDWAIT,
                cleanupCts.Token),
            log,
            startupCancellation,
            cleanupCts.Token);

        var activeSilos = HostedCluster.GetActiveSilos().ToHashSet();
        Assert.True(
            initialSilos.SetEquals(activeSilos),
            $"Reminder test did not restore its baseline topology. Expected: {string.Join(", ", initialSilos.Select(static silo => silo.SiloAddress))}; actual: {string.Join(", ", activeSilos.Select(static silo => silo.SiloAddress))}.");
    }

    protected async Task<InProcessSiloHandle> StopSiloAndStartAdditionalSiloAsync(
        InProcessSiloHandle siloToStop,
        CancellationToken cancellationToken,
        bool startAdditionalSiloOnNewPort = false)
    {
        var stopTask = StopSiloAsync(siloToStop);
        var startTask = StartAdditionalSilosAsync(
            1,
            cancellationToken,
            startAdditionalSiloOnNewPort);
        await Task.WhenAll(stopTask, startTask).WaitAsync(cancellationToken);

        var silo = Assert.Single(await startTask);
        await observer.WaitForTopologyReconciledAsync(
            WaitForLivenessToStabilizeAsync(),
            [silo],
            CHURN_ENDWAIT,
            cancellationToken);
        return silo;
    }

    protected InProcessSiloHandle GetReminderOwner(IAddressable grain, string reminderName)
    {
        var siloAddress = Assert.Single(observer.GetActiveReminderSilos(grain.GetGrainId(), reminderName));
        return HostedCluster.GetSiloForAddress(siloAddress)
            ?? throw new InvalidOperationException($"Could not find reminder owner {siloAddress} in the active test silos.");
    }

    protected Task StopSiloAsync(InProcessSiloHandle silo)
    {
        return HostedCluster.StopSiloAsync(silo);
    }

    protected async Task Test_Reminders_MultiGrainMultiReminders(
        Func<CancellationToken, Task>? afterFirstTick,
        CancellationToken cancellationToken,
        params IReminderTestGrain2[] grains)
    {
        ArgumentNullException.ThrowIfNull(grains);
        Assert.NotEmpty(grains);

        foreach (var grain in grains)
        {
            ArgumentNullException.ThrowIfNull(grain);
            await ExecuteWithRetries(grain.StartReminder, DR, cancellationToken);
        }

        await AdvanceRemindersByTicksAsync(1, cancellationToken, GetReminderIdentities(grains, DR));
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, 1));

        if (afterFirstTick is not null)
        {
            await afterFirstTick(cancellationToken);
        }

        await AdvanceRemindersByTicksAsync(1, cancellationToken, GetReminderIdentities(grains, DR));
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, 2));

        foreach (var grain in grains)
        {
            await ExecuteWithRetries(grain.StartReminder, R1, cancellationToken);
        }

        await AdvanceRemindersByTicksAsync(2, cancellationToken, GetReminderIdentities(grains, DR, R1));
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, 4), (R1, 2));

        foreach (var grain in grains)
        {
            await ExecuteWithRetries(grain.StartReminder, R2, cancellationToken);
        }

        await AdvanceRemindersByTicksAsync(2, cancellationToken, GetReminderIdentities(grains, DR, R1, R2));
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, 6), (R1, 4), (R2, 2));

        await AdvanceRemindersByTicksAsync(1, cancellationToken, GetReminderIdentities(grains, DR, R1, R2));
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, 7), (R1, 5), (R2, 3));

        await StopRemindersAsync(grains, R1, cancellationToken);
        await AdvanceRemindersByTicksAsync(2, cancellationToken, GetReminderIdentities(grains, DR, R2));
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, 9), (R1, 5), (R2, 5));

        await StopRemindersAsync(grains, R2, cancellationToken);
        await AdvanceRemindersByTicksAsync(1, cancellationToken, GetReminderIdentities(grains, DR));
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, 10), (R1, 5), (R2, 5));

        await StopRemindersAsync(grains, DR, cancellationToken);
        await AdvanceReminderTimeAsync(await grains[0].GetReminderPeriod(DR), cancellationToken);
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, 10), (R1, 5), (R2, 5));
    }

    protected async Task PrepareForGrainFailureAsync(CancellationToken cancellationToken, params IAddressable[] grains)
    {
        ArgumentNullException.ThrowIfNull(grains);
        Assert.NotEmpty(grains);

        await WaitForGrainsReachableAsync(cancellationToken, grains);

        foreach (var grain in grains)
        {
            ArgumentNullException.ThrowIfNull(grain);
            this.log.LogInformation("Preparing grain failure test for Grain={Grain}", grain);
            await StartReminderAsync(grain, DR);
        }

        await AdvanceRemindersByTicksAsync((int)failAfter, cancellationToken, GetReminderIdentities(grains, DR));
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, failAfter));
    }

    protected async Task CompleteGrainFailureTestAsync(CancellationToken cancellationToken, params IAddressable[] grains)
    {
        ArgumentNullException.ThrowIfNull(grains);
        Assert.NotEmpty(grains);

        await WaitForGrainsReachableAsync(cancellationToken, grains);
        await CompleteGrainFailureTestWithoutReachabilityRetriesAsync(cancellationToken, grains);
    }

    private async Task CompleteGrainFailureTestWithoutReachabilityRetriesAsync(
        CancellationToken cancellationToken,
        params IAddressable[] grains)
    {
        await AdvanceRemindersByTicksAsync((int)(failCheckAfter - failAfter), cancellationToken, GetReminderIdentities(grains, DR));
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, failCheckAfter));

        await StopRemindersAsync(grains, DR, cancellationToken);
        await AdvanceReminderTimeAsync(
            await GetReminderPeriodAsync(grains[0], DR).WaitAsync(cancellationToken),
            cancellationToken);
        await AssertReminderCountersAsync(grains, cancellationToken, (DR, failCheckAfter));
    }

    private async Task AssertReminderOwnershipAndSchedulesAsync(
        (IAddressable Grain, string ReminderName)[] reminders,
        long expectedTickCount,
        CancellationToken cancellationToken)
    {
        await SynchronizeReminderSchedulesAsync(cancellationToken, reminders);
        var activeSilos = HostedCluster.GetActiveSilos();

        foreach (var reminder in reminders)
        {
            var grainId = reminder.Grain.GetGrainId();
            var actualOwner = Assert.Single(observer.GetActiveReminderSilos(grainId, reminder.ReminderName));

            Assert.Equal(1, observer.GetActiveReminderCount(grainId, reminder.ReminderName));
            Assert.Contains(activeSilos, silo => silo.SiloAddress == actualOwner);
            Assert.Equal(expectedTickCount, (long)observer.GetTickCount(grainId, reminder.ReminderName));
        }
    }

    private void AssertRemindersStopped(
        (IAddressable Grain, string ReminderName)[] reminders,
        long expectedTickCount)
    {
        foreach (var reminder in reminders)
        {
            var grainId = reminder.Grain.GetGrainId();
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminder.ReminderName));
            Assert.Empty(observer.GetActiveReminderSilos(grainId, reminder.ReminderName));
            Assert.Equal(expectedTickCount, (long)observer.GetTickCount(grainId, reminder.ReminderName));
        }
    }

    private async Task<TGrainInterface> GetGrainOwnedBySiloAsync<TGrainInterface>(
        InProcessSiloHandle owner,
        CancellationToken cancellationToken)
        where TGrainInterface : IGrainWithGuidKey
    {
        const int maximumAttempts = 1_000;

        // Select a key in the joined silo's range so the test always removes a real reminder owner
        // while leaving every baseline silo available for subsequent shared-fixture tests.
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var grain = GrainFactory.GetGrain<TGrainInterface>(Guid.NewGuid());
            var reminderStarted = false;
            var selected = false;
            Exception? selectionException = null;
            try
            {
                await StartReminderAsync(grain, DR).WaitAsync(cancellationToken);
                reminderStarted = true;
                await SynchronizeReminderSchedulesAsync(cancellationToken, (grain, DR));
                if (GetReminderOwner(grain, DR).SiloAddress == owner.SiloAddress)
                {
                    selected = true;
                    return grain;
                }
            }
            catch (Exception exception)
            {
                selectionException = exception;
                throw;
            }
            finally
            {
                if (reminderStarted && !selected)
                {
                    using var cleanupCts = new CancellationTokenSource(ENDWAIT);
                    try
                    {
                        await StopRemindersAsync([grain], DR, cleanupCts.Token);
                    }
                    catch (Exception cleanupException) when (selectionException is not null)
                    {
                        log.LogWarning(
                            cleanupException,
                            "Failed to clean up reminder candidate {GrainId} after selection failed",
                            grain.GetGrainId());
                    }
                }
            }
        }

        throw new InvalidOperationException(
            $"Could not select a {typeof(TGrainInterface).Name} reminder grain owned by {owner.SiloAddress} after {maximumAttempts} attempts.");
    }

    private async Task WaitForReminderRangeReconciliationAsync(CancellationToken cancellationToken)
    {
        // Membership convergence does not await the reminder services' queued range-change reconciliation.
        var rangeChangeReconciliations = HostedCluster.GetActiveSilos().Select(silo =>
            silo.ServiceProvider.GetRequiredService<LocalReminderService>()
                .TestOnlyWaitForRangeChangeReconciliation(cancellationToken));
        await Task.WhenAll(rangeChangeReconciliations);
    }

    protected async Task InvokeGrainCallsAfterTopologyConvergenceAsync(
        CancellationToken cancellationToken,
        params Func<Task>[] grainCalls)
    {
        await WaitForTopologyConvergenceAsync(cancellationToken);

        var callTasks = grainCalls.Select(static grainCall => grainCall()).ToArray();
        await Task.WhenAll(callTasks).WaitAsync(cancellationToken);
    }

    private async Task WaitForTopologyConvergenceAsync(CancellationToken cancellationToken)
    {
        // Liveness stabilization covers membership, client gateways, and the in-process grain directory.
        await WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken);
        await HostedCluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(cancellationToken);
        await WaitForReminderRangeReconciliationAsync(cancellationToken);
    }

    private async Task WaitForGrainsReachableAsync(CancellationToken cancellationToken, params IAddressable[] grains)
    {
        Exception? lastException = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await Task.WhenAll(grains.Select(grain => GetReminderPeriodAsync(grain, DR))).WaitAsync(cancellationToken);
                    return;
                }
                catch (Exception exception) when (IsTransientLifecycleException(exception))
                {
                    lastException = exception;
                    log.LogInformation(
                        exception,
                        "Waiting for reminder grains to become reachable after topology change: {Grains}",
                        string.Join(", ", grains.Select(grain => grain.GetGrainId())));
                }

                try
                {
                    await WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken);
                }
                catch (Exception exception) when (IsTransientLifecycleException(exception))
                {
                    lastException = exception;
                }
            }
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out waiting for reminder grains to become reachable after a topology change: {string.Join(", ", grains.Select(grain => grain.GetGrainId()))}.",
                lastException ?? exception);
        }
    }

    private static bool IsTransientLifecycleException(Exception exception)
    {
        return exception is SiloUnavailableException or OrleansMessageRejectionException or ConnectionFailedException
            || exception.InnerException is not null && IsTransientLifecycleException(exception.InnerException);
    }

    protected async Task AdvanceRemindersByTicksAsync(
        int tickCount,
        CancellationToken cancellationToken,
        params (IAddressable Grain, string ReminderName)[] reminders)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tickCount);
        ArgumentNullException.ThrowIfNull(reminders);
        Assert.NotEmpty(reminders);

        for (var i = 0; i < tickCount; i++)
        {
            await SynchronizeReminderSchedulesAsync(cancellationToken, reminders);

            var previousCounters = new long[reminders.Length];
            for (var reminderIndex = 0; reminderIndex < reminders.Length; reminderIndex++)
            {
                var reminder = reminders[reminderIndex];
                previousCounters[reminderIndex] = await GetReminderCounterOrZeroAsync(
                    reminder.Grain,
                    reminder.ReminderName,
                    cancellationToken);
            }

            var tickCompletion = observer.ArmTickCompletion(cancellationToken, reminders);
            var periods = await Task.WhenAll(reminders.Select(reminder =>
                GetReminderPeriodAsync(reminder.Grain, reminder.ReminderName))).WaitAsync(cancellationToken);
            var period = Assert.Single(periods.Distinct());

            log.LogInformation(
                "Advancing reminder time by {Period} for tick {TickNumber}/{TickCount} across {ReminderCount} reminders",
                period,
                i + 1,
                tickCount,
                reminders.Length);
            await AdvanceReminderTimeAsync(period, cancellationToken);
            // TickCompleted is emitted after ReceiveReminder returns, so counters are already persisted.
            await observer.WaitForTickCompletedAsync(tickCompletion).WaitAsync(cancellationToken);

            await SynchronizeReminderSchedulesAsync(cancellationToken, reminders);
            for (var reminderIndex = 0; reminderIndex < reminders.Length; reminderIndex++)
            {
                var reminder = reminders[reminderIndex];
                Assert.Equal(
                    previousCounters[reminderIndex] + 1,
                    await GetReminderCounterAsync(reminder.Grain, reminder.ReminderName).WaitAsync(cancellationToken));
                Assert.Equal(
                    1,
                    observer.GetActiveReminderCount(
                        reminder.Grain.GetGrainId(),
                        reminder.ReminderName));
                var tickSnapshot = tickCompletion.Snapshot[reminderIndex];
                Assert.Equal(
                    tickSnapshot.PreviousCount + 1,
                    observer.GetTickCount(
                        reminder.Grain.GetGrainId(),
                        reminder.ReminderName));
            }
        }
    }

    protected async Task AssertReminderCountersAsync(
        IEnumerable<IAddressable> grains,
        CancellationToken cancellationToken,
        params (string ReminderName, long ExpectedCount)[] expectedCounters)
    {
        ArgumentNullException.ThrowIfNull(grains);
        ArgumentNullException.ThrowIfNull(expectedCounters);

        foreach (var grain in grains)
        {
            ArgumentNullException.ThrowIfNull(grain);
            foreach (var expected in expectedCounters)
            {
                Assert.Equal(
                    expected.ExpectedCount,
                    await GetReminderCounterAsync(grain, expected.ReminderName).WaitAsync(cancellationToken));
            }
        }
    }

    protected Task AssertReminderCountersAsync(
        IEnumerable<IAddressable> grains,
        params (string ReminderName, long ExpectedCount)[] expectedCounters)
        => AssertReminderCountersAsync(grains, TestContext.Current.CancellationToken, expectedCounters);

    protected static (IAddressable Grain, string ReminderName)[] GetReminderIdentities(
        IEnumerable<IAddressable> grains,
        params string[] reminderNames)
    {
        ArgumentNullException.ThrowIfNull(grains);
        ArgumentNullException.ThrowIfNull(reminderNames);
        return
        [
            .. from grain in grains
               from reminderName in reminderNames
               select (grain, reminderName)
        ];
    }

    protected async Task StopRemindersAsync(
        IEnumerable<IAddressable> grains,
        string reminderName,
        CancellationToken cancellationToken)
    {
        var grainArray = grains.ToArray();
        var unregisteredTasks = grainArray.Select(grain =>
            observer.WaitForReminderUnregisteredAsync(grain, reminderName, cancellationToken)).ToArray();
        var quiescenceTasks = grainArray.Select(grain =>
            observer.WaitForReminderQuiescenceAsync(grain, reminderName, cancellationToken)).ToArray();

        foreach (var grain in grainArray)
        {
            await ExecuteWithRetriesStop(
                name => StopReminderAsync(grain, name),
                reminderName,
                cancellationToken);
        }

        await Task.WhenAll(unregisteredTasks);
        await AdvanceReminderTimeAsync(ReminderClock.RefreshReminderListPeriod, cancellationToken);
        try
        {
            await Task.WhenAll(quiescenceTasks);
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            var activeOwners = grainArray.Select(grain =>
                $"{grain.GetGrainId()}={observer.GetActiveReminderCount(grain.GetGrainId(), reminderName)}");
            throw new InvalidOperationException(
                $"Timed out waiting for reminder '{reminderName}' owners to stop: {string.Join(", ", activeOwners)}",
                exception);
        }
    }

    protected async Task<long> WaitForReminderCounterAsync(
        IAddressable grain,
        string reminderName,
        Func<Task<long>> getCounter,
        long minimumCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grain);
        ArgumentNullException.ThrowIfNull(getCounter);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumCount);

        var grainId = grain.GetGrainId();
        long result = 0;
        async Task<bool> Condition(CancellationToken ct)
        {
            try
            {
                result = await getCounter().WaitAsync(ct);
                return result >= minimumCount;
            }

            catch (FileNotFoundException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await Condition(cancellationToken))
            {
                return result;
            }

            var nextTickTarget = observer.GetTickCount(grainId, reminderName) + 1;
            var waitTask = observer.WaitForTickCountAsync(grainId, nextTickTarget, cancellationToken, reminderName);
            await SynchronizeReminderSchedulesAsync(cancellationToken, (grain, reminderName));
            var reminderPeriod = await GetReminderPeriodAsync(grain, reminderName).WaitAsync(cancellationToken);
            await AdvanceReminderTimeAsync(reminderPeriod, cancellationToken);
            await waitTask;
        }
    }

    protected Task<long> WaitForReminderCounterAsync(
        IAddressable grain,
        string reminderName,
        Func<Task<long>> getCounter,
        long minimumCount)
        => WaitForReminderCounterAsync(
            grain,
            reminderName,
            getCounter,
            minimumCount,
            TestContext.Current.CancellationToken);

    protected async Task<long> WaitForAdditionalReminderCounterAsync(
        IAddressable grain,
        string reminderName,
        Func<Task<long>> getCounter,
        long additionalCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grain);
        ArgumentNullException.ThrowIfNull(getCounter);
        ArgumentOutOfRangeException.ThrowIfNegative(additionalCount);

        long currentCount = 0;
        try
        {
            currentCount = await getCounter();
        }

        catch (FileNotFoundException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return await WaitForReminderCounterAsync(
            grain,
            reminderName,
            getCounter,
            currentCount + additionalCount,
            cancellationToken);
    }

    protected Task<long> WaitForAdditionalReminderCounterAsync(
        IAddressable grain,
        string reminderName,
        Func<Task<long>> getCounter,
        long additionalCount)
        => WaitForAdditionalReminderCounterAsync(
            grain,
            reminderName,
            getCounter,
            additionalCount,
            TestContext.Current.CancellationToken);

    protected Task<bool> PerGrainMultiReminderTest(IReminderTestGrain2 g)
        => PerGrainMultiReminderTest(g, TestContext.Current.CancellationToken);

    protected async Task<bool> PerGrainMultiReminderTest(IReminderTestGrain2 g, CancellationToken cancellationToken)
    {
        await Test_Reminders_MultiGrainMultiReminders(
            afterFirstTick: null,
            cancellationToken,
            g);
        return true;
    }

    protected Task AdvanceReminderTimeAsync(TimeSpan amount)
        => AdvanceReminderTimeAsync(amount, TestContext.Current.CancellationToken);

    protected async Task AdvanceReminderTimeAsync(TimeSpan amount, CancellationToken cancellationToken)
    {
        await ReminderClock.AdvanceAsync(amount, cancellationToken);
    }

    protected Task<IAsyncDisposable> PauseReminderTimeAsync()
        => PauseReminderTimeAsync(TestContext.Current.CancellationToken);

    protected async Task<IAsyncDisposable> PauseReminderTimeAsync(CancellationToken cancellationToken)
    {
        return await ReminderClock.FreezeAsync(cancellationToken);
    }

    protected static string Time()
    {
        return DateTime.UtcNow.ToString("hh:mm:ss.fff");
    }

    protected void AssertIsInRange(long val, long lowerLimit, long upperLimit, IGrain grain, string reminderName, TimeSpan sleepFor)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendFormat("Grain: {0} Grain PrimaryKey: {1}, Reminder: {2}, SleepFor: {3} Time now: {4}",
            grain, grain.GetPrimaryKey(), reminderName, sleepFor, Time());
        sb.AppendFormat(
            " -- Expecting value in the range between {0} and {1}, and got value {2}.",
            lowerLimit, upperLimit, val);
        this.log.LogInformation("{Message}", sb.ToString());

        bool tickCountIsInsideRange = lowerLimit <= val && val <= upperLimit;

        if (!tickCountIsInsideRange)
        {
            Assert.True(tickCountIsInsideRange, $"AssertIsInRange: {sb}  -- WHICH IS OUTSIDE RANGE.");
        }
    }

    protected async Task ExecuteWithRetries(
        Func<string, TimeSpan?, bool, Task> function,
        string reminderName,
        CancellationToken cancellationToken,
        TimeSpan? period = null,
        bool validate = false)
    {
        for (long i = 1; i <= retries; i++)
        {
            try
            {
                await function(reminderName, period, validate).WaitAsync(TestConstants.InitTimeout, cancellationToken);
                return; // success ... no need to retry
            }
            catch (AggregateException aggEx)
            {
                foreach (var exception in aggEx.InnerExceptions)
                {
                    await HandleError(exception, i, cancellationToken);
                }
            }
            catch (ReminderException exc)
            {
                await HandleError(exc, i, cancellationToken);
            }
        }

        // execute one last time and bubble up errors if any
        await function(reminderName, period, validate).WaitAsync(TestConstants.InitTimeout, cancellationToken);
    }

    // Func<> doesnt take optional parameters, thats why we need a separate method
    protected async Task ExecuteWithRetriesStop(
        Func<string, Task> function,
        string reminderName,
        CancellationToken cancellationToken)
    {
        for (long i = 1; i <= retries; i++)
        {
            try
            {
                await function(reminderName).WaitAsync(TestConstants.InitTimeout, cancellationToken);
                return; // success ... no need to retry
            }
            catch (Exception exception)
            {
                await HandleError(exception, i, cancellationToken);
            }
        }

        // execute one last time and bubble up errors if any
        await function(reminderName).WaitAsync(TestConstants.InitTimeout, cancellationToken);
    }

    protected async Task StopReminderAndWaitForQuiescenceAsync(
        IAddressable grain,
        string reminderName,
        Func<string, Task> stopReminder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grain);
        ArgumentNullException.ThrowIfNull(stopReminder);

        var unregisteredTask = observer.WaitForReminderUnregisteredAsync(grain, reminderName, cancellationToken);
        await ExecuteWithRetriesStop(stopReminder, reminderName, cancellationToken);
        await unregisteredTask;
        await WaitForReminderQuiescenceAsync(grain, reminderName, cancellationToken);
    }

    protected Task StopReminderAndWaitForQuiescenceAsync(
        IAddressable grain,
        string reminderName,
        Func<string, Task> stopReminder)
        => StopReminderAndWaitForQuiescenceAsync(
            grain,
            reminderName,
            stopReminder,
            TestContext.Current.CancellationToken);

    private async Task WaitForReminderQuiescenceAsync(IAddressable grain, string reminderName, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (observer.GetActiveReminderCount(grain.GetGrainId(), reminderName) > 0)
            {
                var quiescenceTask = observer.WaitForReminderQuiescenceAsync(grain, reminderName, cancellationToken);
                await observer.RefreshActiveServicesAsync(cancellationToken);
                await quiescenceTask;
            }

            await observer.RefreshActiveServicesAsync(cancellationToken);
            if (observer.GetActiveReminderCount(grain.GetGrainId(), reminderName) == 0)
            {
                return;
            }
        }
    }

    private async Task SynchronizeReminderSchedulesAsync(
        CancellationToken cancellationToken,
        params (IAddressable Grain, string ReminderName)[] reminders)
    {
        try
        {
            await observer.WaitForSchedulesArmedAsync(cancellationToken, reminders);
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            var states = reminders.Select(reminder =>
            {
                var grainId = reminder.Grain.GetGrainId();
                return $"{grainId}/{reminder.ReminderName}: " +
                    $"owners={observer.GetActiveReminderCount(grainId, reminder.ReminderName)}, " +
                    $"silos=[{string.Join(", ", observer.GetActiveReminderSilos(grainId, reminder.ReminderName).Select(static silo => silo.ToString()))}], " +
                    $"ticks={observer.GetTickCount(grainId, reminder.ReminderName)}";
            });
            throw new InvalidOperationException(
                $"Timed out synchronizing reminder schedules at {ReminderUtcNow:O}: {string.Join("; ", states)}",
                exception);
        }
    }

    private async Task<bool> HandleError(Exception ex, long i, CancellationToken cancellationToken)
    {
        if (ex is AggregateException aggregateException)
        {
            ex = aggregateException.Flatten().InnerException!;
        }

        if (ex is ReminderException)
        {
            this.log.LogInformation(ex, "Retryable operation failed on attempt {Attempt}", i);
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            return true;
        }

        return false;
    }

    private static Task<TimeSpan> GetReminderPeriodAsync(IAddressable grain, string reminderName)
    {
        return grain switch
        {
            IReminderTestGrain2 reminderTestGrain2 => reminderTestGrain2.GetReminderPeriod(reminderName),
            IReminderTestCopyGrain reminderTestCopyGrain => reminderTestCopyGrain.GetReminderPeriod(reminderName),
            _ => throw new InvalidOperationException($"Unsupported reminder test grain type: {grain.GetType().FullName}")
        };
    }

    private static Task<IGrainReminder> StartReminderAsync(IAddressable grain, string reminderName)
    {
        return grain switch
        {
            IReminderTestGrain2 reminderTestGrain2 => reminderTestGrain2.StartReminder(reminderName),
            IReminderTestCopyGrain reminderTestCopyGrain => reminderTestCopyGrain.StartReminder(reminderName),
            _ => throw new InvalidOperationException($"Unsupported reminder test grain type: {grain.GetType().FullName}")
        };
    }

    private static Task<long> GetReminderCounterAsync(IAddressable grain, string reminderName)
    {
        return grain switch
        {
            IReminderTestGrain2 reminderTestGrain2 => reminderTestGrain2.GetCounter(reminderName),
            IReminderTestCopyGrain reminderTestCopyGrain => reminderTestCopyGrain.GetCounter(reminderName),
            _ => throw new InvalidOperationException($"Unsupported reminder test grain type: {grain.GetType().FullName}")
        };
    }

    private static async Task<long> GetReminderCounterOrZeroAsync(
        IAddressable grain,
        string reminderName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetReminderCounterAsync(grain, reminderName).WaitAsync(cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    }

    private static Task StopReminderAsync(IAddressable grain, string reminderName)
    {
        return grain switch
        {
            IReminderTestGrain2 reminderTestGrain2 => reminderTestGrain2.StopReminder(reminderName),
            IReminderTestCopyGrain reminderTestCopyGrain => reminderTestCopyGrain.StopReminder(reminderName),
            _ => throw new InvalidOperationException($"Unsupported reminder test grain type: {grain.GetType().FullName}")
        };
    }

    private sealed class ReminderEventRecorder : IObserver<ReminderEvents.ReminderEvent>, IDisposable
    {
        private readonly IDisposable _subscription;
        private readonly ConcurrentQueue<ReminderEvents.ReminderEvent> _events = new();

        public ReminderEventRecorder(IObservable<ReminderEvents.ReminderEvent> observable)
        {
            _subscription = observable.Subscribe(this);
        }

        public ReminderEvents.ReminderEvent[] Events => _events.ToArray();

        public void Dispose() => _subscription.Dispose();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ReminderEvents.ReminderEvent value) => _events.Enqueue(value);
    }
}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
