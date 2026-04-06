#nullable enable

using System.Text;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests;

/// <summary>
/// Base class for reminder tests providing common test operations and utilities.
/// Uses <see cref="ReminderDiagnosticObserver"/> for event-driven waiting instead of Task.Delay.
/// </summary>
public class ReminderTests_Base : OrleansTestingBase, IDisposable
{
    protected TestCluster HostedCluster { get; private set; }
    internal static readonly TimeSpan LEEWAY = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan ENDWAIT = TimeSpan.FromMinutes(2);

    internal const string DR = "DEFAULT_REMINDER";
    internal const string R1 = "REMINDER_1";
    internal const string R2 = "REMINDER_2";

    protected const long retries = 3;

    protected const long failAfter = 2; // NOTE: match this sleep with 'failCheckAfter' used in PerGrainFailureTest() so you dont try to get counter immediately after failure as new activation may not have the reminder statistics
    protected const long failCheckAfter = 6; // safe value: 9

    protected ILogger log;
    protected ReminderDiagnosticObserver observer;

    public ReminderTests_Base(BaseTestClusterFixture fixture)
    {
        HostedCluster = fixture.HostedCluster;
        GrainFactory = fixture.GrainFactory;

        var filters = new LoggerFilterOptions();
#if DEBUG
        filters.AddFilter("Storage", LogLevel.Trace);
        filters.AddFilter("Reminder", LogLevel.Trace);
#endif

        log = TestingUtils.CreateDefaultLoggerFactory(TestingUtils.CreateTraceFileName("client", DateTime.Now.ToString("yyyyMMdd_hhmmss")), filters).CreateLogger<ReminderTests_Base>();
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
        for (int i = 0; i < count; i++)
        {
            await observer.WaitForTickCountAsync(grain, 2, DR + "_" + i);
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
        using var cts = new CancellationTokenSource(ENDWAIT);

        Task<bool>[] tasks =
        [
            Task.Run(() => this.PerGrainMultiReminderTestChurn(g1, cts.Token), cts.Token),
            Task.Run(() => this.PerGrainMultiReminderTestChurn(g2, cts.Token), cts.Token),
            Task.Run(() => this.PerGrainMultiReminderTestChurn(g3, cts.Token), cts.Token),
            Task.Run(() => this.PerGrainMultiReminderTestChurn(g4, cts.Token), cts.Token),
            Task.Run(() => this.PerGrainMultiReminderTestChurn(g5, cts.Token), cts.Token),
        ];

        // Wait for all grains to have at least one reminder tick before adding a silo
        await observer.WaitForReminderTickAsync(g1, cancellationToken: cts.Token);
        await observer.WaitForReminderTickAsync(g2, cancellationToken: cts.Token);
        await observer.WaitForReminderTickAsync(g3, cancellationToken: cts.Token);
        await observer.WaitForReminderTickAsync(g4, cancellationToken: cts.Token);
        await observer.WaitForReminderTickAsync(g5, cancellationToken: cts.Token);

        // start another silo ... although it will take it a while before it stabilizes
        log.LogInformation("Starting another silo");
        await this.HostedCluster.StartAdditionalSilosAsync(1, true).WaitAsync(cts.Token);

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

    internal async Task<bool> PerGrainMultiReminderTestChurn(IReminderTestGrain2 g, CancellationToken cancellationToken = default)
    {
        // for churn cases, we do execute start and stop reminders with retries as we don't have the queue-ing
        // functionality implemented on the LocalReminderService yet
        this.log.LogInformation("PerGrainMultiReminderTestChurn Grain={Grain}", g);

        // Start Default Reminder and wait for 2 ticks
        await ExecuteWithRetries(g.StartReminder, DR);
        await observer.WaitForTickCountAsync(g, 2, DR, cancellationToken);

        // Start R1 and wait for 2 ticks
        await ExecuteWithRetries(g.StartReminder, R1);
        await observer.WaitForTickCountAsync(g, 2, R1, cancellationToken);

        // Start R2 and wait for 2 ticks
        await ExecuteWithRetries(g.StartReminder, R2);
        await observer.WaitForTickCountAsync(g, 2, R2, cancellationToken);

        // Wait for 1 more DR tick to verify all reminders are still running
        await observer.WaitForAdditionalTickCountAsync(g, 1, DR, cancellationToken);

        // Stop R1
        await ExecuteWithRetriesStop(g.StopReminder, R1);
        // Wait for 2 more DR ticks to let things settle after R1 stop
        await observer.WaitForAdditionalTickCountAsync(g, 2, DR, cancellationToken);

        // Stop R2
        await ExecuteWithRetriesStop(g.StopReminder, R2);
        // Wait for 1 more DR tick
        await observer.WaitForAdditionalTickCountAsync(g, 1, DR, cancellationToken);

        // Stop Default reminder
        await ExecuteWithRetriesStop(g.StopReminder, DR);

        long lastR1 = await g.GetCounter(R1);
        Assert.True(lastR1 >= 4, $"R1 should have at least 4 ticks, got {lastR1}");

        long lastR2 = await g.GetCounter(R2);
        Assert.True(lastR2 >= 4, $"R2 should have at least 4 ticks, got {lastR2}");

        long lastDR = await g.GetCounter(DR);
        Assert.True(lastDR >= 9, $"DR should have at least 9 ticks, got {lastDR}");

        return true;
    }

    protected async Task<bool> PerGrainFailureTest(IReminderTestGrain2 grain, CancellationToken cancellationToken = default)
    {
        this.log.LogInformation("PerGrainFailureTest Grain={Grain}", grain);

        await grain.StartReminder(DR);
        await observer.WaitForTickCountAsync(grain, (int)failCheckAfter, DR, cancellationToken);
        long last = await grain.GetCounter(DR);
        Assert.True(last >= failCheckAfter, $"Expected at least {failCheckAfter} ticks, got {last}");

        await grain.StopReminder(DR);
        // Brief pause to confirm no more ticks after stopping
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        long curr = await grain.GetCounter(DR);
        // After stopping, at most one extra tick could have been in-flight
        AssertIsInRange(curr, last, last + 1, grain, DR, TimeSpan.FromMilliseconds(200));

        return true;
    }

    protected async Task<bool> PerGrainMultiReminderTest(IReminderTestGrain2 g, CancellationToken cancellationToken = default)
    {
        this.log.LogInformation("PerGrainMultiReminderTest Grain={Grain}", g);

        // Each reminder is started then verified via observer ticks.
        // Once all reminders have been started, stop them one at a time
        // and verify stopped reminders no longer fire.

        // Start Default Reminder and wait for first tick
        await g.StartReminder(DR);
        await observer.WaitForTickCountAsync(g, 1, DR, cancellationToken);
        var reminders = await g.GetReminderStates();
        Assert.True(reminders[DR].Fired.Count >= 1, $"DR should have fired at least 1 time, got {reminders[DR].Fired.Count}");

        // Start R1 and wait for first tick
        await g.StartReminder(R1);
        await observer.WaitForTickCountAsync(g, 1, R1, cancellationToken);
        reminders = await g.GetReminderStates();
        Assert.True(reminders[R1].Fired.Count >= 1, $"R1 should have fired at least 1 time, got {reminders[R1].Fired.Count}");

        // Start R2 and wait for first tick
        await g.StartReminder(R2);
        await observer.WaitForTickCountAsync(g, 1, R2, cancellationToken);
        reminders = await g.GetReminderStates();
        Assert.True(reminders[R2].Fired.Count >= 1, $"R2 should have fired at least 1 time, got {reminders[R2].Fired.Count}");
        Assert.True(reminders[R1].Fired.Count >= 1, $"R1 should still be running, got {reminders[R1].Fired.Count}");
        Assert.True(reminders[DR].Fired.Count >= 1, $"DR should still be running, got {reminders[DR].Fired.Count}");

        // Stop R1 — record its count, then wait for another R2 tick to confirm R2/DR continue
        int r1CountAtStop = reminders[R1].Fired.Count;
        await g.StopReminder(R1);
        await observer.WaitForAdditionalTickCountAsync(g, 1, R2, cancellationToken);
        reminders = await g.GetReminderStates();
        // R1 should be stable (at most 1 in-flight tick)
        Assert.True(reminders[R1].Fired.Count <= r1CountAtStop + 1, $"R1 should have stopped, but count went from {r1CountAtStop} to {reminders[R1].Fired.Count}");
        Assert.True(reminders[R2].Fired.Count >= 2, $"R2 should still be running, got {reminders[R2].Fired.Count}");

        // Stop R2 — record its count, then wait for another DR tick
        int r2CountAtStop = reminders[R2].Fired.Count;
        await g.StopReminder(R2);
        await observer.WaitForAdditionalTickCountAsync(g, 1, DR, cancellationToken);
        reminders = await g.GetReminderStates();
        Assert.True(reminders[R2].Fired.Count <= r2CountAtStop + 1, $"R2 should have stopped, but count went from {r2CountAtStop} to {reminders[R2].Fired.Count}");
        Assert.True(reminders[R1].Fired.Count <= r1CountAtStop + 1, $"R1 should still be stopped");

        // Stop Default reminder
        int drCountAtStop = reminders[DR].Fired.Count;
        await g.StopReminder(DR);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        reminders = await g.GetReminderStates();
        Assert.True(reminders[DR].Fired.Count <= drCountAtStop + 1, $"DR should have stopped, but count went from {drCountAtStop} to {reminders[DR].Fired.Count}");
        Assert.True(reminders[R1].Fired.Count <= r1CountAtStop + 1, $"R1 should still be stopped");
        Assert.True(reminders[R2].Fired.Count <= r2CountAtStop + 1, $"R2 should still be stopped");

        return true;
    }

    protected async Task<bool> PerCopyGrainFailureTest(IReminderTestCopyGrain grain, CancellationToken cancellationToken = default)
    {
        this.log.LogInformation("PerCopyGrainFailureTest Grain={Grain}", grain);

        await grain.StartReminder(DR);
        await observer.WaitForTickCountAsync(grain, (int)failCheckAfter, DR, cancellationToken);
        long last = await grain.GetCounter(DR);
        Assert.Equal(failCheckAfter, last);

        await grain.StopReminder(DR);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        long curr = await grain.GetCounter(DR);
        Assert.Equal(last, curr);

        return true;
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
}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
