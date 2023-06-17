//#define USE_SQL_SERVER

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

namespace UnitTests.TimerTests
{
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
        }

        public IGrainFactory GrainFactory { get; }

        public void Dispose()
        {
            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitWithThrow(TestConstants.InitTimeout);
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
                Assert.True(false, "Removed reminder1, which shouldn't be possible.");
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
            await Task.Delay((period + LEEWAY).Multiply(2)); // giving some leeway, including leeway in each period to minimize flakiness
            for (int i = 0; i < count; i++)
            {
                long curr = await grain.GetCounter(DR + "_" + i);
                Assert.Equal(2, curr);
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

            await Task.Delay(period.Multiply(5));

            // start another silo ... although it will take it a while before it stabilizes
            log.LogInformation("Starting another silo");
            await this.HostedCluster.StartAdditionalSilosAsync(1, true);

            //Block until all tasks complete.
            await Task.WhenAll(tasks).WithTimeout(ENDWAIT);
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

            // Start Default Reminder
            //g.StartReminder(DR, file + "_" + DR).Wait();
            await ExecuteWithRetries(g.StartReminder, DR);
            TimeSpan sleepFor = period.Multiply(2);
            await Task.Delay(sleepFor);
            // Start R1
            //g.StartReminder(R1, file + "_" + R1).Wait();
            await ExecuteWithRetries(g.StartReminder, R1);
            sleepFor = period.Multiply(2);
            await Task.Delay(sleepFor);
            // Start R2
            //g.StartReminder(R2, file + "_" + R2).Wait();
            await ExecuteWithRetries(g.StartReminder, R2);
            sleepFor = period.Multiply(2);
            await Task.Delay(sleepFor);

            sleepFor = period.Multiply(1);
            await Task.Delay(sleepFor);

            // Stop R1
            //g.StopReminder(R1).Wait();
            await ExecuteWithRetriesStop(g.StopReminder, R1);
            sleepFor = period.Multiply(2);
            await Task.Delay(sleepFor);
            // Stop R2
            //g.StopReminder(R2).Wait();
            await ExecuteWithRetriesStop(g.StopReminder, R2);
            sleepFor = period.Multiply(1);
            await Task.Delay(sleepFor);

            // Stop Default reminder
            //g.StopReminder(DR).Wait();
            await ExecuteWithRetriesStop(g.StopReminder, DR);
            sleepFor = period.Multiply(1) + LEEWAY; // giving some leeway
            await Task.Delay(sleepFor);

            long last = await g.GetCounter(R1);
            AssertIsInRange(last, 4, 6, g, R1, sleepFor);

            last = await g.GetCounter(R2);
            AssertIsInRange(last, 4, 6, g, R2, sleepFor);

            last = await g.GetCounter(DR);
            AssertIsInRange(last, 9, 10, g, DR, sleepFor);

            return true;
        }

        protected async Task<bool> PerGrainFailureTest(IReminderTestGrain2 grain)
        {
            TimeSpan period = await grain.GetReminderPeriod(DR);

            this.log.LogInformation("PerGrainFailureTest Period={Period} Grain={Grain}", period, grain);

            await grain.StartReminder(DR);
            TimeSpan sleepFor = period.Multiply(failCheckAfter) + LEEWAY; // giving some leeway
            await Task.Delay(sleepFor);
            long last = await grain.GetCounter(DR);
            AssertIsInRange(last, failCheckAfter - 1, failCheckAfter + 1, grain, DR, sleepFor);

            await grain.StopReminder(DR);
            sleepFor = period.Multiply(2) + LEEWAY; // giving some leeway
            await Task.Delay(sleepFor);
            long curr = await grain.GetCounter(DR);

            AssertIsInRange(curr, last, last + 1, grain, DR, sleepFor);

            return true;
        }

        protected async Task<bool> PerGrainMultiReminderTest(IReminderTestGrain2 g)
        {
            var (dueTime, period) = await g.GetReminderDueTimeAndPeriod(DR);

            this.log.LogInformation("PerGrainMultiReminderTest Period={Period} Grain={Grain}", period, g);

            // Each reminder is started 2 periods after the previous reminder
            // once all reminders have been started, stop them every 2 periods
            // except the default reminder, which we stop after 3 periods instead
            // just to test and break the symmetry

            // Start Default Reminder
            await g.StartReminder(DR);
            TimeSpan sleepFor = dueTime + period;
            await Task.Delay(sleepFor);
            var reminders = await g.GetReminderStates();
            var (min, max) = GetExpectedRange(1);
            AssertIsInRange(reminders[DR].Fired.Count, min, max, g, DR, sleepFor);

            // Start R1
            await g.StartReminder(R1);
            await Task.Delay(sleepFor);
            reminders = await g.GetReminderStates();
            (min, max) = GetExpectedRange(1);
            AssertIsInRange(reminders[R1].Fired.Count, min, max, g, R1, sleepFor);

            // Start R2
            await g.StartReminder(R2);
            await Task.Delay(sleepFor);
            reminders = await g.GetReminderStates();
            (min, max) = GetExpectedRange(2);
            AssertIsInRange(reminders[R1].Fired.Count, min, max, g, R1, sleepFor);
            (min, max) = GetExpectedRange(1);
            AssertIsInRange(reminders[R2].Fired.Count, min, max, g, R2, sleepFor);
            (min, max) = GetExpectedRange(3);
            AssertIsInRange(reminders[DR].Fired.Count, min, max, g, DR, sleepFor);

            // Stop R1
            await g.StopReminder(R1);
            await Task.Delay(sleepFor);
            reminders = await g.GetReminderStates();
            (min, max) = GetExpectedRange(2);
            AssertIsInRange(reminders[R1].Fired.Count, min, max, g, R1, sleepFor);
            AssertIsInRange(reminders[R2].Fired.Count, min, max, g, R2, sleepFor);
            (min, max) = GetExpectedRange(4);
            AssertIsInRange(reminders[DR].Fired.Count, min, max, g, DR, sleepFor);

            // Stop R2
            await g.StopReminder(R2);
            await Task.Delay(sleepFor);
            reminders = await g.GetReminderStates();
            (min, max) = GetExpectedRange(2);
            AssertIsInRange(reminders[R1].Fired.Count, min, max, g, R1, sleepFor);
            AssertIsInRange(reminders[R2].Fired.Count, min, max, g, R2, sleepFor);
            (min, max) = GetExpectedRange(5);
            AssertIsInRange(reminders[DR].Fired.Count, min, max, g, DR, sleepFor);

            // Stop Default reminder
            await g.StopReminder(DR);
            await Task.Delay(sleepFor);
            reminders = await g.GetReminderStates();
            (min, max) = GetExpectedRange(2);
            AssertIsInRange(reminders[R1].Fired.Count, min, max, g, R1, sleepFor);
            AssertIsInRange(reminders[R2].Fired.Count, min, max, g, R2, sleepFor);
            (min, max) = GetExpectedRange(5);
            AssertIsInRange(reminders[DR].Fired.Count, min, max, g, DR, sleepFor);

            return true;

            (int MinFires, int MaxFires) GetExpectedRange(int numPeriods)
            {
                var minExpected = ((sleepFor - LEEWAY) * numPeriods - dueTime) / period;
                var maxExpected = ((sleepFor + LEEWAY) * numPeriods - dueTime) / period;
                return ((int)Math.Floor(minExpected), (int)Math.Ceiling(maxExpected));
            }
        }

        protected async Task<bool> PerCopyGrainFailureTest(IReminderTestCopyGrain grain)
        {
            TimeSpan period = await grain.GetReminderPeriod(DR);

            this.log.LogInformation("PerCopyGrainFailureTest Period={Period} Grain={Grain}", period, grain);

            await grain.StartReminder(DR);
            await Task.Delay(period.Multiply(failCheckAfter) + LEEWAY); // giving some leeway
            long last = await grain.GetCounter(DR);
            Assert.Equal(failCheckAfter, last);  // "{0} CopyGrain {1} Reminder {2}" // Time(), grain.GetPrimaryKey(), DR);

            await grain.StopReminder(DR);
            await Task.Delay(period.Multiply(2) + LEEWAY); // giving some leeway
            long curr = await grain.GetCounter(DR);
            Assert.Equal(last, curr); // "{0} CopyGrain {1} Reminder {2}", Time(), grain.GetPrimaryKey(), DR);

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
                    await function(reminderName, period, validate).WithTimeout(TestConstants.InitTimeout);
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
            await function(reminderName, period, validate).WithTimeout(TestConstants.InitTimeout);
        }

        // Func<> doesnt take optional parameters, thats why we need a separate method
        protected async Task ExecuteWithRetriesStop(Func<string, Task> function, string reminderName)
        {
            for (long i = 1; i <= retries; i++)
            {
                try
                {
                    await function(reminderName).WithTimeout(TestConstants.InitTimeout);
                    return; // success ... no need to retry
                }
                catch (Exception exception)
                {
                    await HandleError(exception, i);
                }
            }

            // execute one last time and bubble up errors if any
            await function(reminderName).WithTimeout(TestConstants.InitTimeout);
        }

        private async Task<bool> HandleError(Exception ex, long i)
        {
            if (ex is AggregateException)
            {
                ex = ((AggregateException)ex).Flatten().InnerException;
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
}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
