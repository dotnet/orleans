#nullable enable

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.ReminderService;
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
public class ReminderTestsBase : OrleansTestingBase, IDisposable
{
    protected InProcessTestCluster HostedCluster { get; }
    protected static readonly TimeSpan LEEWAY = TimeSpan.FromMilliseconds(500);
    protected readonly TimeSpan ENDWAIT;
    protected readonly TimeSpan CHURN_ENDWAIT;

    protected const string DR = "DEFAULT_REMINDER";
    protected const string R1 = "REMINDER_1";
    protected const string R2 = "REMINDER_2";

    protected const long retries = 3;

    protected const long failAfter = 2;
    protected const long failCheckAfter = 6;
    protected ILogger log;
    protected ReminderDiagnosticObserver observer;
    private ReminderTestClock ReminderClock { get; }

    public ReminderTestsBase(ReminderTestClock reminderClock, InProcessTestCluster hostedCluster, int timeoutMultiplier = 1)
    {
        ArgumentNullException.ThrowIfNull(reminderClock);
        ArgumentNullException.ThrowIfNull(hostedCluster);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMultiplier, 1);

        var grainFactory = hostedCluster.Client;
        if (grainFactory is null)
        {
            throw new InvalidOperationException($"{nameof(InProcessTestCluster)} client is not initialized.");
        }

        HostedCluster = hostedCluster;
        GrainFactory = grainFactory;
        ReminderClock = reminderClock;
        ENDWAIT = TimeSpan.FromMinutes(2 * timeoutMultiplier);
        CHURN_ENDWAIT = TimeSpan.FromMinutes(5 * timeoutMultiplier);

        var filters = new LoggerFilterOptions();
#if DEBUG
        filters.AddFilter("Storage", LogLevel.Trace);
        filters.AddFilter("Reminder", LogLevel.Trace);
#endif

        log = TestingUtils.CreateDefaultLoggerFactory(TestingUtils.CreateTraceFileName("client", DateTime.Now.ToString("yyyyMMdd_hhmmss")), filters).CreateLogger<ReminderTestsBase>();
        observer = ReminderDiagnosticObserver.Create();
    }

    public IGrainFactory GrainFactory { get; }

    protected DateTimeOffset ReminderUtcNow => ReminderClock.UtcNow;

    public void Dispose()
    {
        observer.Dispose();

        // ReminderTable.Clear() cannot be called from a non-Orleans thread,
        // so we must proxy the call through a grain.
        var controlProxy = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        controlProxy.EraseReminderTable().WaitAsync(TestConstants.InitTimeout).Wait();
    }

    public async Task Test_Reminders_Basic_StopByRef()
    {
        IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        IGrainReminder r1 = await grain.StartReminder(DR);
        IGrainReminder r2 = await grain.StartReminder(DR);
        try
        {
            // First handle should now be out of date once the second handle to the same reminder was obtained
            await grain.StopReminder(r1);
            Assert.Fail("Removed reminder1, which shouldn't be possible.");
        }
        catch (Exception exc)
        {
            log.LogInformation(exc, "Couldn't remove {Reminder}, as expected.", r1);
        }

        await grain.StopReminder(r2);
        log.LogInformation("Removed reminder2 successfully");

        // trying to see if readreminder works
        _ = await grain.StartReminder(DR);
        _ = await grain.StartReminder(DR);
        _ = await grain.StartReminder(DR);
        _ = await grain.StartReminder(DR);

        IGrainReminder? r = await grain.GetReminderObject(DR);
        await grain.StopReminder(r!);
        log.LogInformation("Removed got reminder successfully");
    }

    public async Task Test_Reminders_Basic_ListOps()
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

        await Task.WhenAll(startReminderTasks);
        // do comparison on strings
        List<string> registered = (from reminder in startReminderTasks select reminder.Result.ReminderName).ToList();

        log.LogInformation("Waited");

        List<IGrainReminder> remindersList = await grain.GetRemindersList();
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
        using var cts = new CancellationTokenSource(ENDWAIT);
        var reminderNames = Enumerable.Range(0, count).Select(i => DR + "_" + i).ToArray();
        await AdvanceRemindersByTicksAsync(2, cts.Token, GetReminderIdentities([grain], reminderNames));

        // Verify via grain counters
        foreach (var reminderName in reminderNames)
        {
            Assert.Equal(2, await grain.GetCounter(reminderName));
        }
    }

    public async Task Test_Reminders_1J_MultiGrainMultiReminders()
    {
        IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        using var cts = new CancellationTokenSource(CHURN_ENDWAIT);
        InProcessSiloHandle? additionalSilo = null;
        try
        {
            await Test_Reminders_MultiGrainMultiReminders(
                async cancellationToken =>
                {
                    await using (await PauseReminderTimeAsync(cancellationToken))
                    {
                        log.LogInformation("Starting another silo");
                        additionalSilo = Assert.Single(await this.StartAdditionalSilosAndWaitForReminderServicesAsync(
                            1,
                            cancellationToken,
                            startAdditionalSiloOnNewPort: true));
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
            if (additionalSilo is not null)
            {
                using var cleanupCts = new CancellationTokenSource(CHURN_ENDWAIT);
                await StopSiloAsync(additionalSilo).WaitAsync(cleanupCts.Token);
                await WaitForLivenessToStabilizeAsync().WaitAsync(cleanupCts.Token);
            }
        }
    }

    public async Task Test_Reminders_2J_MultiGrainMultiReminders()
    {
        IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var initialSilos = HostedCluster.GetActiveSilos().ToHashSet();
        using var cts = new CancellationTokenSource(CHURN_ENDWAIT);
        Task<List<InProcessSiloHandle>>? startSilosTask = null;
        try
        {
            await Test_Reminders_MultiGrainMultiReminders(
                async cancellationToken =>
                {
                    await using (await PauseReminderTimeAsync(cancellationToken))
                    {
                        log.LogInformation("Starting 2 extra silos");
                        startSilosTask = StartAdditionalSilosAsync(2, startAdditionalSiloOnNewPort: true);
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
            if (startSilosTask is not null)
            {
                try
                {
                    using var startupCompletionCts = new CancellationTokenSource(CHURN_ENDWAIT);
                    await startSilosTask.WaitAsync(startupCompletionCts.Token);
                }
                catch (Exception exception)
                {
                    log.LogInformation(exception, "Additional silo startup did not complete successfully before cleanup.");
                }

                var additionalSilos = HostedCluster.GetActiveSilos()
                    .Where(silo => !initialSilos.Contains(silo))
                    .ToArray();
                if (additionalSilos.Length > 0)
                {
                    using var cleanupCts = new CancellationTokenSource(CHURN_ENDWAIT);
                    await Task.WhenAll(additionalSilos.Select(StopSiloAsync)).WaitAsync(cleanupCts.Token);
                    await WaitForLivenessToStabilizeAsync().WaitAsync(cleanupCts.Token);
                }
            }
        }
    }

    public async Task Test_Reminders_ReminderNotFound()
    {
        IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        // request a reminder that does not exist
        IGrainReminder? reminder = await g1.GetReminderObject("blarg");
        Assert.Null(reminder);
    }

    public async Task Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ENDWAIT);

        var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        var grainId = grain.GetGrainId();

        await grain.StartReminder(DR);
        var firstTickCount = await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 1, cts.Token);
        Assert.Equal(1, observer.GetActiveReminderCount(grainId, DR));

        using (var recorder = new ReminderEventRecorder(ReminderEvents.AllEvents))
        {
            await grain.StartReminder(DR);
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

    protected Task<List<InProcessSiloHandle>> StartAdditionalSilosAsync(int silosToStart, bool startAdditionalSiloOnNewPort = false)
    {
        return HostedCluster.StartSilosAsync(silosToStart);
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
        var startSilosTask = StartAdditionalSilosAsync(silosToStart, startAdditionalSiloOnNewPort);
        return await WaitForAdditionalSilosAndReminderServicesAsync(startSilosTask, cancellationToken);
    }

    private async Task<List<InProcessSiloHandle>> WaitForAdditionalSilosAndReminderServicesAsync(
        Task<List<InProcessSiloHandle>> startSilosTask,
        CancellationToken cancellationToken)
    {
        var result = await startSilosTask.WaitAsync(cancellationToken);
        var reminderServicesStarted = result.Select(silo =>
            observer.WaitForReminderServiceStartedAsync(cancellationToken, silo.SiloAddress));
        await Task.WhenAll(
            Task.WhenAll(reminderServicesStarted),
            WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken));
        return result;
    }

    protected async Task<InProcessSiloHandle> StopSiloAndStartAdditionalSiloAsync(
        InProcessSiloHandle siloToStop,
        CancellationToken cancellationToken,
        bool startAdditionalSiloOnNewPort = false)
    {
        var stopTask = StopSiloAsync(siloToStop);
        var startTask = StartAdditionalSilosAsync(1, startAdditionalSiloOnNewPort);
        await Task.WhenAll(stopTask, startTask).WaitAsync(cancellationToken);

        var silo = Assert.Single(await startTask);
        await Task.WhenAll(
            observer.WaitForReminderServiceStartedAsync(cancellationToken, silo.SiloAddress),
            WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken));
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
            await ExecuteWithRetries(grain.StartReminder, DR);
        }

        await AdvanceRemindersByTicksAsync(1, cancellationToken, GetReminderIdentities(grains, DR));
        await AssertReminderCountersAsync(grains, (DR, 1));

        if (afterFirstTick is not null)
        {
            await afterFirstTick(cancellationToken);
        }

        await AdvanceRemindersByTicksAsync(1, cancellationToken, GetReminderIdentities(grains, DR));
        await AssertReminderCountersAsync(grains, (DR, 2));

        foreach (var grain in grains)
        {
            await ExecuteWithRetries(grain.StartReminder, R1);
        }

        await AdvanceRemindersByTicksAsync(2, cancellationToken, GetReminderIdentities(grains, DR, R1));
        await AssertReminderCountersAsync(grains, (DR, 4), (R1, 2));

        foreach (var grain in grains)
        {
            await ExecuteWithRetries(grain.StartReminder, R2);
        }

        await AdvanceRemindersByTicksAsync(2, cancellationToken, GetReminderIdentities(grains, DR, R1, R2));
        await AssertReminderCountersAsync(grains, (DR, 6), (R1, 4), (R2, 2));

        await AdvanceRemindersByTicksAsync(1, cancellationToken, GetReminderIdentities(grains, DR, R1, R2));
        await AssertReminderCountersAsync(grains, (DR, 7), (R1, 5), (R2, 3));

        await StopRemindersAsync(grains, R1, cancellationToken);
        await AdvanceRemindersByTicksAsync(2, cancellationToken, GetReminderIdentities(grains, DR, R2));
        await AssertReminderCountersAsync(grains, (DR, 9), (R1, 5), (R2, 5));

        await StopRemindersAsync(grains, R2, cancellationToken);
        await AdvanceRemindersByTicksAsync(1, cancellationToken, GetReminderIdentities(grains, DR));
        await AssertReminderCountersAsync(grains, (DR, 10), (R1, 5), (R2, 5));

        await StopRemindersAsync(grains, DR, cancellationToken);
        await AdvanceReminderTimeAsync(await grains[0].GetReminderPeriod(DR), cancellationToken);
        await AssertReminderCountersAsync(grains, (DR, 10), (R1, 5), (R2, 5));
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
        await AssertReminderCountersAsync(grains, (DR, failAfter));
    }

    protected async Task CompleteGrainFailureTestAsync(CancellationToken cancellationToken, params IAddressable[] grains)
    {
        ArgumentNullException.ThrowIfNull(grains);
        Assert.NotEmpty(grains);

        await WaitForGrainsReachableAsync(cancellationToken, grains);
        await AdvanceRemindersByTicksAsync((int)(failCheckAfter - failAfter), cancellationToken, GetReminderIdentities(grains, DR));
        await AssertReminderCountersAsync(grains, (DR, failCheckAfter));

        await StopRemindersAsync(grains, DR, cancellationToken);
        await AdvanceReminderTimeAsync(await GetReminderPeriodAsync(grains[0], DR), cancellationToken);
        await AssertReminderCountersAsync(grains, (DR, failCheckAfter));
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
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
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

            var counterWaitTasks = new List<Task>(reminders.Length);
            var previousTickCounts = new int[reminders.Length];
            var tickTasks = new Task[reminders.Length];
            for (var reminderIndex = 0; reminderIndex < reminders.Length; reminderIndex++)
            {
                var reminder = reminders[reminderIndex];
                previousTickCounts[reminderIndex] = observer.GetTickCount(
                    reminder.Grain.GetGrainId(),
                    reminder.ReminderName);
                tickTasks[reminderIndex] = observer.WaitForTickCountAsync(
                    reminder.Grain,
                    previousTickCounts[reminderIndex] + 1,
                    cancellationToken,
                    reminder.ReminderName);
            }

            foreach (var reminder in reminders)
            {
                var current = await GetReminderCounterOrZeroAsync(reminder.Grain, reminder.ReminderName);
                counterWaitTasks.Add(GetReminderCounterWaitTask(
                    reminder.Grain,
                    reminder.ReminderName,
                    current + 1,
                    cancellationToken));
            }

            var periods = await Task.WhenAll(reminders.Select(reminder =>
                GetReminderPeriodAsync(reminder.Grain, reminder.ReminderName)));
            var period = Assert.Single(periods.Distinct());

            log.LogInformation(
                "Advancing reminder time by {Period} for tick {TickNumber}/{TickCount} across {ReminderCount} reminders",
                period,
                i + 1,
                tickCount,
                reminders.Length);
            await AdvanceReminderTimeAsync(period, cancellationToken);
            await Task.WhenAll(Task.WhenAll(counterWaitTasks), Task.WhenAll(tickTasks));

            await SynchronizeReminderSchedulesAsync(cancellationToken, reminders);
            for (var reminderIndex = 0; reminderIndex < reminders.Length; reminderIndex++)
            {
                var reminder = reminders[reminderIndex];
                Assert.Equal(
                    1,
                    observer.GetActiveReminderCount(
                        reminder.Grain.GetGrainId(),
                        reminder.ReminderName));
                Assert.Equal(
                    previousTickCounts[reminderIndex] + 1,
                    observer.GetTickCount(
                        reminder.Grain.GetGrainId(),
                        reminder.ReminderName));
            }
        }
    }

    protected async Task AssertReminderCountersAsync(
        IEnumerable<IAddressable> grains,
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
                    await GetReminderCounterAsync(grain, expected.ReminderName));
            }
        }
    }

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
                reminderName);
        }

        await Task.WhenAll(unregisteredTasks);
        await AdvanceReminderTimeAsync(ReminderClock.RefreshReminderListPeriod, cancellationToken);
        try
        {
            await Task.WhenAll(quiescenceTasks);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
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
        CancellationToken cancellationToken = default)
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
                result = await getCounter();
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
            var reminderPeriod = await GetReminderPeriodAsync(grain, reminderName);
            await AdvanceReminderTimeAsync(reminderPeriod, cancellationToken);
            await waitTask;
        }
    }

    protected async Task<long> WaitForAdditionalReminderCounterAsync(
        IAddressable grain,
        string reminderName,
        Func<Task<long>> getCounter,
        long additionalCount,
        CancellationToken cancellationToken = default)
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

    protected async Task<bool> PerGrainMultiReminderTest(IReminderTestGrain2 g, CancellationToken cancellationToken = default)
    {
        await Test_Reminders_MultiGrainMultiReminders(
            afterFirstTick: null,
            cancellationToken,
            g);
        return true;
    }

    protected async Task AdvanceReminderTimeAsync(TimeSpan amount, CancellationToken cancellationToken = default)
    {
        await ReminderClock.AdvanceAsync(amount, cancellationToken);
    }

    protected async Task<IAsyncDisposable> PauseReminderTimeAsync(CancellationToken cancellationToken = default)
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
        TimeSpan? period = null,
        bool validate = false)
    {
        for (long i = 1; i <= retries; i++)
        {
            try
            {
                await function(reminderName, period, validate).WaitAsync(TestConstants.InitTimeout);
                return; // success ... no need to retry
            }
            catch (AggregateException aggEx)
            {
                foreach (var exception in aggEx.InnerExceptions)
                {
                    await HandleError(exception, i);
                }
            }
            catch (ReminderException exc)
            {
                await HandleError(exc, i);
            }
        }

        // execute one last time and bubble up errors if any
        await function(reminderName, period, validate).WaitAsync(TestConstants.InitTimeout);
    }

    // Func<> doesnt take optional parameters, thats why we need a separate method
    protected async Task ExecuteWithRetriesStop(Func<string, Task> function, string reminderName)
    {
        for (long i = 1; i <= retries; i++)
        {
            try
            {
                await function(reminderName).WaitAsync(TestConstants.InitTimeout);
                return; // success ... no need to retry
            }
            catch (Exception exception)
            {
                await HandleError(exception, i);
            }
        }

        // execute one last time and bubble up errors if any
        await function(reminderName).WaitAsync(TestConstants.InitTimeout);
    }

    protected async Task StopReminderAndWaitForQuiescenceAsync(
        IAddressable grain,
        string reminderName,
        Func<string, Task> stopReminder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grain);
        ArgumentNullException.ThrowIfNull(stopReminder);

        var unregisteredTask = observer.WaitForReminderUnregisteredAsync(grain, reminderName, cancellationToken);
        await ExecuteWithRetriesStop(stopReminder, reminderName);
        await unregisteredTask;
        await WaitForReminderQuiescenceAsync(grain, reminderName, cancellationToken);
    }

    private async Task WaitForReminderQuiescenceAsync(IAddressable grain, string reminderName, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (observer.GetActiveReminderCount(grain.GetGrainId(), reminderName) > 0)
            {
                var quiescenceTask = observer.WaitForReminderQuiescenceAsync(grain, reminderName, cancellationToken);
                await RefreshReminderServicesAsync(cancellationToken);
                await quiescenceTask;
            }

            await RefreshReminderServicesAsync(cancellationToken);
            if (observer.GetActiveReminderCount(grain.GetGrainId(), reminderName) == 0)
            {
                return;
            }
        }
    }

    private async Task RefreshReminderServicesAsync(CancellationToken cancellationToken)
    {
        // Each refresh scans the backing reminder table. Serialize this test-only barrier so that
        // constrained provider emulators observe the same completed-refresh invariant without an artificial query burst.
        foreach (var silo in HostedCluster.GetActiveSilos())
        {
            await silo.ServiceProvider.GetRequiredService<LocalReminderService>().TestOnlyRefresh().WaitAsync(cancellationToken);
        }
    }

    private async Task SynchronizeReminderSchedulesAsync(
        CancellationToken cancellationToken,
        params (IAddressable Grain, string ReminderName)[] reminders)
    {
        await RefreshReminderServicesAsync(cancellationToken);
        try
        {
            await Task.WhenAll(reminders.Select(async reminder =>
            {
                await observer.WaitForActiveReminderCountAsync(
                    reminder.Grain,
                    1,
                    cancellationToken,
                    reminder.ReminderName);
                await observer.WaitForLocalReminderScheduleAsync(
                    reminder.Grain,
                    reminder.ReminderName,
                    cancellationToken);
            }));
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
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

    private async Task<bool> HandleError(Exception ex, long i)
    {
        if (ex is AggregateException aggregateException)
        {
            ex = aggregateException.Flatten().InnerException!;
        }

        if (ex is ReminderException)
        {
            this.log.LogInformation(ex, "Retryable operation failed on attempt {Attempt}", i);
            await Task.Delay(TimeSpan.FromMilliseconds(10));
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

    private static async Task<long> GetReminderCounterOrZeroAsync(IAddressable grain, string reminderName)
    {
        try
        {
            return await GetReminderCounterAsync(grain, reminderName);
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    }

    private async Task GetReminderCounterWaitTask(
        IAddressable grain,
        string reminderName,
        long target,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await GetReminderCounterOrZeroAsync(grain, reminderName);
                if (!HostedCluster.TryGetGrainContext(grain.GetGrainId(), out var grainContext))
                {
                    await Task.Yield();
                    continue;
                }

                var completed = grainContext.GrainInstance switch
                {
                    ReminderTestGrain2 reminderTestGrain => await reminderTestGrain.WaitForCounterForTestAsync(reminderName, target, current, cancellationToken),
                    ReminderTestCopyGrain reminderTestCopyGrain => await reminderTestCopyGrain.WaitForCounterForTestAsync(reminderName, target, current, cancellationToken),
                    { } instance => throw new InvalidOperationException($"Unexpected grain instance type {instance.GetType()} for grain {grain.GetGrainId()}."),
                    null => false
                };
                if (completed)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var current = await GetReminderCounterOrZeroAsync(grain, reminderName);
            var activeOwners = observer.GetActiveReminderCount(grain.GetGrainId(), reminderName);
            throw new InvalidOperationException(
                $"Timed out waiting for reminder '{reminderName}' on grain {grain.GetGrainId()} to reach counter {target}. Current counter: {current}. Active owners: {activeOwners}.",
                exception);
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
