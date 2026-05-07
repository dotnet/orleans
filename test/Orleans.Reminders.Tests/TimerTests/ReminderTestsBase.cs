#nullable enable

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
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
    protected static readonly TimeSpan ENDWAIT = TimeSpan.FromMinutes(2);
    protected static readonly TimeSpan CHURN_ENDWAIT = TimeSpan.FromMinutes(5);

    protected const string DR = "DEFAULT_REMINDER";
    protected const string R1 = "REMINDER_1";
    protected const string R2 = "REMINDER_2";

    protected const long retries = 3;

    protected const long failAfter = 2; // NOTE: match this sleep with 'failCheckAfter' used in PerGrainFailureTest() so you dont try to get counter immediately after failure as new activation may not have the reminder statistics
    protected const long failCheckAfter = 6; // safe value: 9

    protected ILogger log;
    protected ReminderDiagnosticObserver observer;
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
        observer = ReminderDiagnosticObserver.Create();
    }

    public IGrainFactory GrainFactory { get; }

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

        IGrainReminder r = await grain.GetReminderObject(DR);
        await grain.StopReminder(r);
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
        for (int i = 0; i < count; i++)
        {
            await WaitForReminderCounterAsync(grain, DR + "_" + i, () => grain.GetCounter(DR + "_" + i), 2, cts.Token);
        }

        // Verify via grain counters
        for (int i = 0; i < count; i++)
        {
            long curr = await grain.GetCounter(DR + "_" + i);
            Assert.True(curr >= 2, $"Reminder {DR}_{i} should have fired at least 2 times, but fired {curr}");
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

        Task<bool>[] tasks =
        [
            Task.Run(() => this.PerGrainMultiReminderTestChurn(g1, cts.Token), cts.Token),
            Task.Run(() => this.PerGrainMultiReminderTestChurn(g2, cts.Token), cts.Token),
            Task.Run(() => this.PerGrainMultiReminderTestChurn(g3, cts.Token), cts.Token),
            Task.Run(() => this.PerGrainMultiReminderTestChurn(g4, cts.Token), cts.Token),
            Task.Run(() => this.PerGrainMultiReminderTestChurn(g5, cts.Token), cts.Token),
        ];

        // Wait for all grains to have at least one reminder tick before adding a silo
        await WaitForInitialReminderTicksAsync(cts.Token, g1, g2, g3, g4, g5);

        // start another silo ... although it will take it a while before it stabilizes
        await using (await PauseReminderTimeAsync(cts.Token))
        {
            log.LogInformation("Starting another silo");
            await this.StartAdditionalSilosAsync(1, true).WaitAsync(cts.Token);
            await this.WaitForLivenessToStabilizeAsync().WaitAsync(cts.Token);
        }

        //Block until all tasks complete.
        await Task.WhenAll(tasks).WaitAsync(cts.Token);
    }

    public async Task Test_Reminders_ReminderNotFound()
    {
        IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

        // request a reminder that does not exist
        IGrainReminder reminder = await g1.GetReminderObject("blarg");
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
            await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), firstTickCount + 1, cts.Token);

            Assert.Contains(recorder.Events, evt => evt is ReminderEvents.Registered registered && registered.GrainId == grainId && registered.ReminderName == DR);
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

    protected InProcessSiloHandle GetSecondarySilo()
    {
        foreach (var silo in HostedCluster.GetActiveSilos())
        {
            if (silo.InstanceNumber != 0)
            {
                return silo;
            }
        }

        throw new InvalidOperationException("Expected at least one non-primary silo.");
    }

    protected Task StopSiloAsync(InProcessSiloHandle silo)
    {
        return HostedCluster.StopSiloAsync(silo);
    }

    protected async Task WaitForInitialReminderTicksAsync(CancellationToken cancellationToken, params IReminderTestGrain2[] grains)
    {
        ArgumentNullException.ThrowIfNull(grains);

        foreach (var grain in grains)
        {
            ArgumentNullException.ThrowIfNull(grain);
            await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 1, cancellationToken);
        }
    }

    protected async Task<bool> PerGrainMultiReminderTestChurn(IReminderTestGrain2 g, CancellationToken cancellationToken = default)
    {
        // for churn cases, we do execute start and stop reminders with retries as we don't have the queue-ing
        // functionality implemented on the LocalReminderService yet
        this.log.LogInformation("PerGrainMultiReminderTestChurn Grain={Grain}", g);

        // Start Default Reminder and wait for 2 persisted counter increments.
        await ExecuteWithRetries(g.StartReminder, DR);
        await WaitForAdditionalReminderCounterAsync(g, DR, () => g.GetCounter(DR), 2, cancellationToken);

        // Start R1 and wait for 2 persisted counter increments.
        await ExecuteWithRetries(g.StartReminder, R1);
        await WaitForAdditionalReminderCounterAsync(g, R1, () => g.GetCounter(R1), 2, cancellationToken);

        // Start R2 and wait for 2 persisted counter increments.
        await ExecuteWithRetries(g.StartReminder, R2);
        await WaitForAdditionalReminderCounterAsync(g, R2, () => g.GetCounter(R2), 2, cancellationToken);

        // Wait for 1 more DR counter increment to verify all reminders are still running.
        await WaitForAdditionalReminderCounterAsync(g, DR, () => g.GetCounter(DR), 1, cancellationToken);

        // If this test asserts that R1 reached 4 ticks, wait on R1 itself rather than
        // inferring progress from DR. This turns the old flaky floor into an explicit contract.
        await WaitForReminderCounterAsync(g, R1, () => g.GetCounter(R1), 4, cancellationToken);

        // Stop R1
        await StopReminderAndWaitForQuiescenceAsync(g, R1, g.StopReminder, cancellationToken);
        // Wait for 2 more DR counter increments to let things settle after R1 stop.
        await WaitForAdditionalReminderCounterAsync(g, DR, () => g.GetCounter(DR), 2, cancellationToken);

        // Stop R2
        await StopReminderAndWaitForQuiescenceAsync(g, R2, g.StopReminder, cancellationToken);
        // Wait for 1 more DR counter increment.
        await WaitForAdditionalReminderCounterAsync(g, DR, () => g.GetCounter(DR), 1, cancellationToken);

        // Stop Default reminder
        await StopReminderAndWaitForQuiescenceAsync(g, DR, g.StopReminder, cancellationToken);

        long lastR1 = await g.GetCounter(R1);
        const long minimumReminder1Ticks = 4;
        Assert.True(lastR1 >= minimumReminder1Ticks, $"R1 should have at least {minimumReminder1Ticks} ticks, got {lastR1}");

        long lastR2 = await g.GetCounter(R2);
        // R2 only gets its initial two observed ticks plus the DR-based settle window after R1 stops,
        // so the observer-driven sequence guarantees 3 ticks here instead of the old 4-tick floor.
        const long minimumReminder2Ticks = 3;
        Assert.True(lastR2 >= minimumReminder2Ticks, $"R2 should have at least {minimumReminder2Ticks} ticks, got {lastR2}");

        long lastDR = await g.GetCounter(DR);
        // The observer-driven waits no longer spend two full periods between each step, so
        // DEFAULT_REMINDER is guaranteed to reach 8 ticks here instead of the old 9-tick floor.
        const long minimumDefaultReminderTicks = 8;
        Assert.True(lastDR >= minimumDefaultReminderTicks, $"DR should have at least {minimumDefaultReminderTicks} ticks, got {lastDR}");

        return true;
    }

    protected async Task<bool> PerGrainFailureTest(IReminderTestGrain2 grain, CancellationToken cancellationToken = default)
    {
        this.log.LogInformation("PerGrainFailureTest Grain={Grain}", grain);

        await grain.StartReminder(DR);
        long last = await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), failCheckAfter, cancellationToken);
        Assert.True(last >= failCheckAfter, $"Expected at least {failCheckAfter} ticks, got {last}");

        await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, cancellationToken);
        var stoppedCount = await grain.GetCounter(DR);
        await AdvanceReminderTimeAsync(await GetReminderPeriodAsync(grain, DR), cancellationToken);

        long curr = await grain.GetCounter(DR);
        Assert.Equal(stoppedCount, curr);

        return true;
    }

    protected async Task<long> WaitForReminderCounterAsync(IAddressable grain, string reminderName, Func<Task<long>> getCounter, long minimumCount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grain);
        ArgumentNullException.ThrowIfNull(getCounter);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumCount);

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

            var nextTickTarget = observer.GetTickCount(grain.GetGrainId(), reminderName) + 1;
            var waitTask = observer.WaitForTickCountAsync(grain, nextTickTarget, cancellationToken, reminderName);
            await AdvanceReminderTimeAsync(await GetReminderPeriodAsync(grain, reminderName), cancellationToken);
            await waitTask;
        }
    }

    protected async Task<long> WaitForAdditionalReminderCounterAsync(IAddressable grain, string reminderName, Func<Task<long>> getCounter, long additionalCount, CancellationToken cancellationToken = default)
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

        return await WaitForReminderCounterAsync(grain, reminderName, getCounter, currentCount + additionalCount, cancellationToken);
    }

    protected async Task<bool> PerGrainMultiReminderTest(IReminderTestGrain2 g, CancellationToken cancellationToken = default)
    {
        this.log.LogInformation("PerGrainMultiReminderTest Grain={Grain}", g);

        // Each reminder is started then verified via observer ticks.
        // Once all reminders have been started, stop them one at a time
        // and verify stopped reminders no longer fire.

        // Start Default Reminder and wait for first tick
        await g.StartReminder(DR);
        await WaitForReminderCounterAsync(g, DR, () => g.GetCounter(DR), 1, cancellationToken);
        var reminders = await g.GetReminderStates();
        Assert.True(reminders[DR].Fired.Count >= 1, $"DR should have fired at least 1 time, got {reminders[DR].Fired.Count}");

        // Start R1 and wait for first tick
        await g.StartReminder(R1);
        await WaitForReminderCounterAsync(g, R1, () => g.GetCounter(R1), 1, cancellationToken);
        reminders = await g.GetReminderStates();
        Assert.True(reminders[R1].Fired.Count >= 1, $"R1 should have fired at least 1 time, got {reminders[R1].Fired.Count}");

        // Start R2 and wait for first tick
        await g.StartReminder(R2);
        await WaitForReminderCounterAsync(g, R2, () => g.GetCounter(R2), 1, cancellationToken);
        reminders = await g.GetReminderStates();
        Assert.True(reminders[R2].Fired.Count >= 1, $"R2 should have fired at least 1 time, got {reminders[R2].Fired.Count}");
        Assert.True(reminders[R1].Fired.Count >= 1, $"R1 should still be running, got {reminders[R1].Fired.Count}");
        Assert.True(reminders[DR].Fired.Count >= 1, $"DR should still be running, got {reminders[DR].Fired.Count}");

        // Stop R1, wait for quiescence, then confirm R2/DR continue while R1 remains stable.
        await StopReminderAndWaitForQuiescenceAsync(g, R1, g.StopReminder, cancellationToken);
        reminders = await g.GetReminderStates();
        int r1CountAtStop = reminders[R1].Fired.Count;
        await WaitForAdditionalReminderCounterAsync(g, R2, () => g.GetCounter(R2), 1, cancellationToken);
        reminders = await g.GetReminderStates();
        Assert.Equal(r1CountAtStop, reminders[R1].Fired.Count);
        Assert.True(reminders[R2].Fired.Count >= 2, $"R2 should still be running, got {reminders[R2].Fired.Count}");

        // Stop R2, wait for quiescence, then confirm DR continues while R1/R2 remain stable.
        await StopReminderAndWaitForQuiescenceAsync(g, R2, g.StopReminder, cancellationToken);
        reminders = await g.GetReminderStates();
        int r2CountAtStop = reminders[R2].Fired.Count;
        await WaitForAdditionalReminderCounterAsync(g, DR, () => g.GetCounter(DR), 1, cancellationToken);
        reminders = await g.GetReminderStates();
        Assert.Equal(r2CountAtStop, reminders[R2].Fired.Count);
        Assert.Equal(r1CountAtStop, reminders[R1].Fired.Count);

        // Stop Default reminder
        await StopReminderAndWaitForQuiescenceAsync(g, DR, g.StopReminder, cancellationToken);
        reminders = await g.GetReminderStates();
        int drCountAtStop = reminders[DR].Fired.Count;
        await AdvanceReminderTimeAsync(await GetReminderPeriodAsync(g, DR), cancellationToken);

        reminders = await g.GetReminderStates();
        Assert.Equal(drCountAtStop, reminders[DR].Fired.Count);
        Assert.Equal(r1CountAtStop, reminders[R1].Fired.Count);
        Assert.Equal(r2CountAtStop, reminders[R2].Fired.Count);

        return true;
    }

    protected async Task<bool> PerCopyGrainFailureTest(IReminderTestCopyGrain grain, CancellationToken cancellationToken = default)
    {
        this.log.LogInformation("PerCopyGrainFailureTest Grain={Grain}", grain);

        await grain.StartReminder(DR);
        long last = await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), failCheckAfter, cancellationToken);
        Assert.True(last >= failCheckAfter, $"Expected at least {failCheckAfter} ticks, got {last}");

        await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, cancellationToken);
        var stoppedCount = await grain.GetCounter(DR);
        await AdvanceReminderTimeAsync(await GetReminderPeriodAsync(grain, DR), cancellationToken);

        long curr = await grain.GetCounter(DR);
        Assert.Equal(stoppedCount, curr);

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

    protected async Task ExecuteWithRetries(Func<string, TimeSpan?, bool, Task> function, string reminderName, TimeSpan? period = null, bool validate = false)
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

    protected async Task StopReminderAndWaitForQuiescenceAsync(IAddressable grain, string reminderName, Func<string, Task> stopReminder, CancellationToken cancellationToken = default)
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
            while (observer.GetActiveReminderCount(grain.GetGrainId(), reminderName) > 0)
            {
                var quiescenceTask = observer.WaitForReminderQuiescenceAsync(grain, reminderName, cancellationToken);
                if (quiescenceTask.IsCompleted)
                {
                    await quiescenceTask;
                    break;
                }

                await AdvanceReminderTimeAsync(ReminderClock.RefreshReminderListPeriod, cancellationToken);
            }

            await AdvanceReminderTimeAsync(ReminderClock.RefreshReminderListPeriod, cancellationToken);
            if (observer.GetActiveReminderCount(grain.GetGrainId(), reminderName) == 0)
            {
                return;
            }
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
            await Task.Delay(TimeSpan.FromMilliseconds(10)); // sleep a bit before retrying
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
