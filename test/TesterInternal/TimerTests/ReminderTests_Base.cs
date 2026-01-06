#nullable enable
//#define USE_SQL_SERVER

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Orleans.Diagnostics;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests
{
    /// <summary>
    /// Base class for reminder tests providing common test operations and utilities.
    /// </summary>
    public class ReminderTests_Base : OrleansTestingBase, IDisposable
    {
        protected TestCluster HostedCluster { get; private set; }
        internal static readonly TimeSpan LEEWAY = TimeSpan.FromMilliseconds(500); // the experiment shouldnt be that long that the sums of leeways exceeds a period
        internal static readonly TimeSpan ENDWAIT = TimeSpan.FromMinutes(5);

        internal const string DR = "DEFAULT_REMINDER";
        internal const string R1 = "REMINDER_1";
        internal const string R2 = "REMINDER_2";

        protected const long retries = 3;

        protected const long failAfter = 2; // NOTE: match this sleep with 'failCheckAfter' used in PerGrainFailureTest() so you dont try to get counter immediately after failure as new activation may not have the reminder statistics
        protected const long failCheckAfter = 6; // safe value: 9

        protected ILogger log;
        
        /// <summary>
        /// FakeTimeProvider for controlling virtual time in tests.
        /// When non-null, time can be advanced instantly instead of waiting.
        /// </summary>
        protected FakeTimeProvider? FakeTimeProvider { get; }

        /// <summary>
        /// DiagnosticEventCollector for observing reminder events.
        /// Used for event-driven waiting instead of polling.
        /// </summary>
        protected DiagnosticEventCollector? DiagnosticCollector { get; }

        public ReminderTests_Base(BaseTestClusterFixture fixture) : this(fixture, null, null)
        {
        }

        public ReminderTests_Base(BaseTestClusterFixture fixture, FakeTimeProvider? fakeTimeProvider) 
            : this(fixture, fakeTimeProvider, null)
        {
        }

        public ReminderTests_Base(BaseTestClusterFixture fixture, FakeTimeProvider? fakeTimeProvider, DiagnosticEventCollector? diagnosticCollector)
        {
            HostedCluster = fixture.HostedCluster;
            GrainFactory = fixture.GrainFactory;
            FakeTimeProvider = fakeTimeProvider;
            DiagnosticCollector = diagnosticCollector;

            var filters = new LoggerFilterOptions();
#if DEBUG
            filters.AddFilter("Storage", LogLevel.Trace);
            filters.AddFilter("Reminder", LogLevel.Trace);
#endif

            log = TestingUtils.CreateDefaultLoggerFactory(TestingUtils.CreateTraceFileName("client", DateTime.Now.ToString("yyyyMMdd_hhmmss")), filters).CreateLogger<ReminderTests_Base>();
        }

        public IGrainFactory GrainFactory { get; }

        /// <summary>
        /// Waits for a reminder to reach a specific tick count using event-driven waiting.
        /// When FakeTimeProvider and DiagnosticCollector are available, advances virtual time
        /// and waits for TickCompleted events. Otherwise falls back to polling.
        /// </summary>
        protected async Task<int> WaitForReminderTickCountAsync(IReminderTestGrain2 grain, string reminderName, int expectedCount, TimeSpan timeout)
        {
            var grainId = grain.GetGrainId();
            
            // If we have both FakeTimeProvider and DiagnosticCollector, use event-driven waiting
            if (FakeTimeProvider != null && DiagnosticCollector != null)
            {
                return await WaitForReminderTickCountWithEventsAsync(grainId, reminderName, expectedCount, timeout);
            }
            
            // Fallback to polling
            return await WaitForReminderTickCountWithPollingAsync(grain, reminderName, expectedCount, timeout);
        }

        /// <summary>
        /// Waits for a reminder to reach a specific tick count using event-driven waiting.
        /// When FakeTimeProvider and DiagnosticCollector are available, advances virtual time
        /// and waits for TickCompleted events. Otherwise falls back to polling.
        /// </summary>
        protected async Task<int> WaitForReminderTickCountAsync(IReminderTestCopyGrain grain, string reminderName, int expectedCount, TimeSpan timeout)
        {
            var grainId = grain.GetGrainId();
            
            // If we have both FakeTimeProvider and DiagnosticCollector, use event-driven waiting
            if (FakeTimeProvider != null && DiagnosticCollector != null)
            {
                return await WaitForReminderTickCountWithEventsAsync(grainId, reminderName, expectedCount, timeout);
            }
            
            // Fallback to polling
            return await WaitForReminderTickCountWithPollingAsync(grain, reminderName, expectedCount, timeout);
        }

        /// <summary>
        /// Event-driven waiting: advances virtual time and waits for TickCompleted events.
        /// This method advances time incrementally and gives the async infrastructure time to process.
        /// </summary>
        private async Task<int> WaitForReminderTickCountWithEventsAsync(GrainId grainId, string reminderName, int expectedCount, TimeSpan timeout)
        {
            // Count existing tick events for this grain/reminder
            int currentCount = CountTickCompletedEvents(grainId, reminderName);
            
            if (currentCount >= expectedCount)
            {
                log.LogInformation("WaitForReminderTickCount (events): {ReminderName} already at {Count} ticks (expected {Expected})", 
                    reminderName, currentCount, expectedCount);
                return currentCount;
            }

            // Default reminder config: period = 12s, dueTime = 10s (period - 2s)
            // We need to advance enough virtual time and give the async machinery time to process.
            // Use a pattern similar to the ActivationCollector tests: advance time, wait briefly for processing.
            const int SecondsPerAdvance = 2;
            
            // Calculate maximum iterations needed:
            // - First tick needs ~10s (dueTime)
            // - Subsequent ticks need ~12s each (period)
            // - For expectedCount ticks: ~10 + (expectedCount * 12) seconds of virtual time
            // - With 2 seconds per advance: (10 + expectedCount * 12) / 2 iterations
            // - Add generous buffer for timing variations
            var maxIterations = (10 + expectedCount * 12) / SecondsPerAdvance + 50;
            
            for (int i = 0; i < maxIterations && currentCount < expectedCount; i++)
            {
                // Advance time in increments to trigger timers
                FakeTimeProvider!.Advance(TimeSpan.FromSeconds(SecondsPerAdvance));
                
                // Give the async timer infrastructure time to process
                // Use Task.Delay with a real timeout to allow continuations to run
                await Task.Delay(50);
                
                // Yield to allow any pending async operations to complete
                await Task.Yield();
                
                // Recount tick events
                currentCount = CountTickCompletedEvents(grainId, reminderName);
            }

            if (currentCount >= expectedCount)
            {
                log.LogInformation("WaitForReminderTickCount (events): {ReminderName} reached {Count} ticks (expected {Expected})", 
                    reminderName, currentCount, expectedCount);
            }
            else
            {
                log.LogWarning("WaitForReminderTickCount (events): {ReminderName} only reached {Count} ticks (expected {Expected}) after {Iterations} iterations", 
                    reminderName, currentCount, expectedCount, maxIterations);
            }
            
            return currentCount;
        }

        /// <summary>
        /// Counts TickCompleted events for a specific grain and reminder.
        /// </summary>
        private int CountTickCompletedEvents(GrainId grainId, string reminderName)
        {
            if (DiagnosticCollector == null) return 0;
            
            return DiagnosticCollector.GetEvents(OrleansRemindersDiagnostics.EventNames.TickCompleted)
                .Count(e => e.Payload is ReminderTickCompletedEvent evt 
                    && evt.GrainId == grainId 
                    && evt.ReminderName == reminderName);
        }

        /// <summary>
        /// Polling-based waiting: repeatedly checks the tick count with delays.
        /// Handles communication exceptions gracefully (e.g., when a silo is killed during failure tests).
        /// </summary>
        private async Task<int> WaitForReminderTickCountWithPollingAsync(IReminderTestGrain2 grain, string reminderName, int expectedCount, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            int lastKnownCount = 0;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var count = await grain.GetReminderTickCount(reminderName);
                    lastKnownCount = count;
                    if (count >= expectedCount)
                    {
                        log.LogInformation("WaitForReminderTickCount (polling): {ReminderName} reached {Count} ticks (expected {Expected})", 
                            reminderName, count, expectedCount);
                        return count;
                    }
                }
                catch (Exception ex) when (ex is TimeoutException || ex is OrleansMessageRejectionException || ex.InnerException is TimeoutException)
                {
                    // During failure tests, the grain's silo might be killed. The grain will re-activate
                    // on another silo, so we just continue polling. Log at debug level to avoid noise.
                    log.LogDebug(ex, "WaitForReminderTickCount (polling): Communication error while checking {ReminderName}, will retry", reminderName);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            // Final attempt to get count
            try
            {
                var finalCount = await grain.GetReminderTickCount(reminderName);
                log.LogWarning("WaitForReminderTickCount (polling): {ReminderName} timed out with {Count} ticks (expected {Expected})", 
                    reminderName, finalCount, expectedCount);
                return finalCount;
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OrleansMessageRejectionException || ex.InnerException is TimeoutException)
            {
                log.LogWarning(ex, "WaitForReminderTickCount (polling): {ReminderName} timed out and final check failed. Last known count: {LastKnown} (expected {Expected})", 
                    reminderName, lastKnownCount, expectedCount);
                return lastKnownCount;
            }
        }

        /// <summary>
        /// Polling-based waiting: repeatedly checks the tick count with delays.
        /// Handles communication exceptions gracefully (e.g., when a silo is killed during failure tests).
        /// </summary>
        private async Task<int> WaitForReminderTickCountWithPollingAsync(IReminderTestCopyGrain grain, string reminderName, int expectedCount, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            int lastKnownCount = 0;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var count = await grain.GetReminderTickCount(reminderName);
                    lastKnownCount = count;
                    if (count >= expectedCount)
                    {
                        log.LogInformation("WaitForReminderTickCount (polling): {ReminderName} reached {Count} ticks (expected {Expected})", 
                            reminderName, count, expectedCount);
                        return count;
                    }
                }
                catch (Exception ex) when (ex is TimeoutException || ex is OrleansMessageRejectionException || ex.InnerException is TimeoutException)
                {
                    // During failure tests, the grain's silo might be killed. The grain will re-activate
                    // on another silo, so we just continue polling. Log at debug level to avoid noise.
                    log.LogDebug(ex, "WaitForReminderTickCount (polling): Communication error while checking {ReminderName}, will retry", reminderName);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            // Final attempt to get count
            try
            {
                var finalCount = await grain.GetReminderTickCount(reminderName);
                log.LogWarning("WaitForReminderTickCount (polling): {ReminderName} timed out with {Count} ticks (expected {Expected})", 
                    reminderName, finalCount, expectedCount);
                return finalCount;
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OrleansMessageRejectionException || ex.InnerException is TimeoutException)
            {
                log.LogWarning(ex, "WaitForReminderTickCount (polling): {ReminderName} timed out and final check failed. Last known count: {LastKnown} (expected {Expected})", 
                    reminderName, lastKnownCount, expectedCount);
                return lastKnownCount;
            }
        }

        /// <summary>
        /// Advances time by the specified duration. If FakeTimeProvider is available, advances virtual time;
        /// otherwise waits for real time to pass.
        /// </summary>
        protected async Task AdvanceTimeAsync(TimeSpan duration)
        {
            if (FakeTimeProvider != null)
            {
                FakeTimeProvider.Advance(duration);
                // Small yield to allow async timers to process
                await Task.Delay(1);
            }
            else
            {
                await Task.Delay(duration);
            }
        }

        public void Dispose()
        {
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
                // First handle should now be out of date once the seconf handle to the same reminder was obtained
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

            // do some time tests as well
            log.LogInformation("Time tests");
            TimeSpan period = await grain.GetReminderPeriod(DR);
            
            // Wait for all reminders to fire 2 times. Since all reminders were started at the same time,
            // wait for a fixed duration that allows 2 periods to pass for all of them.
            // Using Task.Delay ensures we wait for real time to pass (important for tests without FakeTimeProvider).
            await Task.Delay((period + LEEWAY).Multiply(2));
            
            for (int i = 0; i < count; i++)
            {
                long curr = await grain.GetCounter(DR + "_" + i);
                Assert.True(curr >= 2, $"Expected at least 2 ticks for {DR}_{i}, got {curr}");
            }
        }

        public async Task Test_Reminders_1J_MultiGrainMultiReminders()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

            TimeSpan period = await g1.GetReminderPeriod(DR);

            Task<bool>[] tasks =
            {
                Task.Run(() => this.PerGrainMultiReminderTestChurn(g1)),
                Task.Run(() => this.PerGrainMultiReminderTestChurn(g2)),
                Task.Run(() => this.PerGrainMultiReminderTestChurn(g3)),
                Task.Run(() => this.PerGrainMultiReminderTestChurn(g4)),
                Task.Run(() => this.PerGrainMultiReminderTestChurn(g5)),
            };

            await AdvanceTimeAsync(period.Multiply(5));

            // start another silo ... although it will take it a while before it stabilizes
            log.LogInformation("Starting another silo");
            await this.HostedCluster.StartAdditionalSilosAsync(1, true);

            //Block until all tasks complete.
            await Task.WhenAll(tasks).WaitAsync(ENDWAIT);
        }

        public async Task Test_Reminders_ReminderNotFound()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

            // request a reminder that does not exist
            IGrainReminder reminder = await g1.GetReminderObject("blarg");
            Assert.Null(reminder);
        }

        internal async Task<bool> PerGrainMultiReminderTestChurn(IReminderTestGrain2 g)
        {
            // for churn cases, we do execute start and stop reminders with retries as we don't have the queue-ing 
            // functionality implemented on the LocalReminderService yet
            TimeSpan period = await g.GetReminderPeriod(DR);
            this.log.LogInformation("PerGrainMultiReminderTestChurn Period={Period} Grain={Grain}", period, g);
            var timeout = ENDWAIT;

            // Start Default Reminder and wait for 2 ticks
            await ExecuteWithRetries(g.StartReminder, DR);
            await WaitForReminderTickCountAsync(g, DR, 2, timeout);
            
            // Start R1 and wait for DR to have 4 ticks
            await ExecuteWithRetries(g.StartReminder, R1);
            await WaitForReminderTickCountAsync(g, DR, 4, timeout);
            
            // Start R2 and wait for DR to have 6 ticks, R1 to have 2 ticks
            await ExecuteWithRetries(g.StartReminder, R2);
            await WaitForReminderTickCountAsync(g, DR, 6, timeout);
            await WaitForReminderTickCountAsync(g, R1, 2, timeout);
            
            // Wait for one more period worth of ticks
            await WaitForReminderTickCountAsync(g, DR, 7, timeout);

            // Stop R1 and wait for DR to have 9 ticks
            await ExecuteWithRetriesStop(g.StopReminder, R1);
            await WaitForReminderTickCountAsync(g, DR, 9, timeout);
            
            // Stop R2
            await ExecuteWithRetriesStop(g.StopReminder, R2);
            await WaitForReminderTickCountAsync(g, DR, 10, timeout);

            // Stop Default reminder
            await ExecuteWithRetriesStop(g.StopReminder, DR);

            // Verify final counts - R1 ran for ~5 periods, R2 ran for ~4 periods, DR ran for 10 periods
            var reminders = await g.GetReminderStates();
            Assert.True(reminders[R1].Fired.Count >= 4, $"R1 should have at least 4 ticks, got {reminders[R1].Fired.Count}");
            Assert.True(reminders[R2].Fired.Count >= 3, $"R2 should have at least 3 ticks, got {reminders[R2].Fired.Count}");
            Assert.True(reminders[DR].Fired.Count >= 10, $"DR should have at least 10 ticks, got {reminders[DR].Fired.Count}");

            return true;
        }

        protected async Task<bool> PerGrainFailureTest(IReminderTestGrain2 grain)
        {
            TimeSpan period = await grain.GetReminderPeriod(DR);

            this.log.LogInformation("PerGrainFailureTest Period={Period} Grain={Grain}", period, grain);
            var timeout = ENDWAIT;

            await grain.StartReminder(DR);
            // Wait for failCheckAfter ticks using event-driven waiting
            await WaitForReminderTickCountAsync(grain, DR, (int)failCheckAfter, timeout);
            long last = await grain.GetCounter(DR);
            // In real-time execution, we may overshoot due to timing variance while polling
            // So we only check that we have at least the minimum expected ticks
            Assert.True(last >= failCheckAfter - 1, $"Expected at least {failCheckAfter - 1} ticks, got {last}");

            await grain.StopReminder(DR);
            // After stopping, wait a bit to ensure no more ticks come through
            // We can't use event-driven waiting here since no more ticks should come
            // But we can reduce this delay since we only need to verify the reminder stopped
            await AdvanceTimeAsync(period.Multiply(2) + LEEWAY);
            long curr = await grain.GetCounter(DR);

            Assert.True(curr >= last && curr <= last + 1, $"Expected counter to stay near {last} after stopping, got {curr}");

            return true;
        }

        protected async Task<bool> PerGrainMultiReminderTest(IReminderTestGrain2 g)
        {
            var (dueTime, period) = await g.GetReminderDueTimeAndPeriod(DR);

            this.log.LogInformation("PerGrainMultiReminderTest Period={Period} Grain={Grain}", period, g);
            var timeout = ENDWAIT;

            // Each reminder is started 2 periods after the previous reminder
            // once all reminders have been started, stop them every 2 periods
            // except the default reminder, which we stop after 3 periods instead
            // just to test and break the symmetry

            // Start Default Reminder and wait for 1 tick
            await g.StartReminder(DR);
            await WaitForReminderTickCountAsync(g, DR, 1, timeout);
            var reminders = await g.GetReminderStates();
            Assert.True(reminders[DR].Fired.Count >= 1, $"DR should have at least 1 tick, got {reminders[DR].Fired.Count}");

            // Start R1 and wait for R1 to have 1 tick
            await g.StartReminder(R1);
            await WaitForReminderTickCountAsync(g, R1, 1, timeout);
            reminders = await g.GetReminderStates();
            Assert.True(reminders[R1].Fired.Count >= 1, $"R1 should have at least 1 tick, got {reminders[R1].Fired.Count}");

            // Start R2 and wait for R2 to have 1 tick
            await g.StartReminder(R2);
            await WaitForReminderTickCountAsync(g, R2, 1, timeout);
            reminders = await g.GetReminderStates();
            Assert.True(reminders[R1].Fired.Count >= 1, $"R1 should have at least 1 tick, got {reminders[R1].Fired.Count}");
            Assert.True(reminders[R2].Fired.Count >= 1, $"R2 should have at least 1 tick, got {reminders[R2].Fired.Count}");

            // Stop R1 - capture R1's count before stopping
            var r1CountBeforeStop = reminders[R1].Fired.Count;
            await g.StopReminder(R1);
            // Wait for R2 to get another tick to confirm time has passed
            await WaitForReminderTickCountAsync(g, R2, 2, timeout);
            reminders = await g.GetReminderStates();
            // R1 should not have increased much after stopping
            Assert.True(reminders[R1].Fired.Count <= r1CountBeforeStop + 1, $"R1 should not tick much after stopping");

            // Stop R2 - capture R2's count before stopping
            var r2CountBeforeStop = reminders[R2].Fired.Count;
            await g.StopReminder(R2);
            // Wait for DR to get more ticks
            await WaitForReminderTickCountAsync(g, DR, 5, timeout);
            reminders = await g.GetReminderStates();
            // R2 should not have increased much after stopping
            Assert.True(reminders[R2].Fired.Count <= r2CountBeforeStop + 1, $"R2 should not tick much after stopping");

            // Stop Default reminder
            var drCountBeforeStop = reminders[DR].Fired.Count;
            await g.StopReminder(DR);
            // Brief delay to ensure DR has stopped
            await AdvanceTimeAsync(period + LEEWAY);
            reminders = await g.GetReminderStates();
            Assert.True(reminders[DR].Fired.Count <= drCountBeforeStop + 1, $"DR should not tick much after stopping");

            return true;
        }

        protected async Task<bool> PerCopyGrainFailureTest(IReminderTestCopyGrain grain)
        {
            TimeSpan period = await grain.GetReminderPeriod(DR);

            this.log.LogInformation("PerCopyGrainFailureTest Period={Period} Grain={Grain}", period, grain);
            var timeout = ENDWAIT;

            await grain.StartReminder(DR);
            // Wait for failCheckAfter ticks using event-driven waiting
            await WaitForReminderTickCountAsync(grain, DR, (int)failCheckAfter, timeout);
            long last = await grain.GetCounter(DR);
            Assert.True(last >= failCheckAfter, $"Expected at least {failCheckAfter} ticks, got {last}");

            await grain.StopReminder(DR);
            // After stopping, wait a bit to ensure no more ticks come through
            await AdvanceTimeAsync(period.Multiply(2) + LEEWAY);
            long curr = await grain.GetCounter(DR);
            Assert.True(curr <= last + 1, $"Expected counter to stay near {last} after stopping, got {curr}");

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
            if (ex is AggregateException aggEx)
            {
                ex = aggEx.Flatten().InnerException ?? ex;
            }

            if (ex is ReminderException)
            {
                this.log.LogInformation(ex, "Retryable operation failed on attempt {Attempt}", i);
                await Task.Delay(TimeSpan.FromMilliseconds(10)); // sleep a bit before retrying
                return true;
            }

            return false;
        }

        /// <summary>
        /// Waits for all specified grains to have received at least the specified number of reminder ticks.
        /// Uses event-driven waiting via ReminderDiagnosticObserver instead of Thread.Sleep.
        /// </summary>
        /// <param name="observer">The reminder diagnostic observer to use.</param>
        /// <param name="grains">The grains to wait for.</param>
        /// <param name="reminderName">The reminder name to check.</param>
        /// <param name="minTickCount">The minimum number of ticks each grain should have received.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        protected async Task WaitForGrainsToReceiveTicksAsync(
            ReminderDiagnosticObserver observer,
            IEnumerable<IAddressable> grains,
            string reminderName,
            int minTickCount,
            TimeSpan timeout)
        {
            var grainList = grains.ToList();
            var tasks = grainList.Select(grain => 
                observer.WaitForTickCountAsync(grain, minTickCount, reminderName, timeout));
            await Task.WhenAll(tasks);
            
            log.LogInformation(
                "All {Count} grains have received at least {MinTicks} ticks for reminder {ReminderName}",
                grainList.Count, minTickCount, reminderName);
        }
    }
}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
