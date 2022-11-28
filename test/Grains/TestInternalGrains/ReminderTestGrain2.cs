using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Services;
using Orleans.Timers;
using UnitTests.GrainInterfaces;


#pragma warning disable 612,618
namespace UnitTests.Grains
{
    // NOTE: if you make any changes here, copy them to ReminderTestCopyGrain
    public class ReminderTestGrain2 : Grain, IReminderTestGrain2, IRemindable
    {
        private readonly IReminderTable reminderTable;

        private readonly IReminderRegistry unvalidatedReminderRegistry;
        Dictionary<string, ReminderState> allReminders;
        Dictionary<string, long> sequence;
        private TimeSpan period;

        private static long aCCURACY = 50 * TimeSpan.TicksPerMillisecond; // when we use ticks to compute sequence numbers, we might get wrong results as timeouts don't happen with precision of ticks  ... we keep this as a leeway

        private IOptions<ReminderOptions> reminderOptions;

        private ILogger logger;
        private string _id; // used to distinguish during debugging between multiple activations of the same grain

        private string filePrefix;

        public ReminderTestGrain2(IServiceProvider services, IReminderTable reminderTable, ILoggerFactory loggerFactory)
        {
            this.reminderTable = reminderTable;
            this.unvalidatedReminderRegistry = new UnvalidatedReminderRegistry(services);
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
            this.reminderOptions = services.GetService<IOptions<ReminderOptions>>();
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this._id = Guid.NewGuid().ToString();
            this.allReminders = new Dictionary<string, ReminderState>();
            this.sequence = new Dictionary<string, long>();
            this.period = GetDefaultPeriod(this.logger);
            this.logger.LogInformation("OnActivateAsync.");
            this.filePrefix = "g" + this.GrainId.ToString().Replace('/', '_') + "_";
            return GetMissingReminders();
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            this.logger.LogInformation("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, ReminderState>> GetReminderStates() => Task.FromResult(allReminders);

        public async Task<IGrainReminder> StartReminder(string reminderName, TimeSpan? p = null, bool validate = false)
        {
            TimeSpan usePeriod = p ?? this.period;
            this.logger.LogInformation("Starting reminder {ReminderName}.", reminderName);
            TimeSpan dueTime;
            if (reminderOptions.Value.MinimumReminderPeriod < TimeSpan.FromSeconds(2))
                dueTime = TimeSpan.FromSeconds(2) - reminderOptions.Value.MinimumReminderPeriod;
            else dueTime = usePeriod - TimeSpan.FromSeconds(2);

            IGrainReminder r;
            if (validate)
                r = await this.RegisterOrUpdateReminder(reminderName, dueTime, usePeriod);
            else
                r = await this.unvalidatedReminderRegistry.RegisterOrUpdateReminder(GrainId, reminderName, dueTime, usePeriod);

            this.allReminders[reminderName] = new(r);
            this.sequence[reminderName] = 0;

            string fileName = GetFileName(reminderName);
            File.Delete(fileName); // if successfully started, then remove any old data
            this.logger.LogInformation("Started reminder {Reminder}", r);
            return r;
        }

        public Task ReceiveReminder(string reminderName, TickStatus status)
        {
            // it can happen that due to failure, when a new activation is created,
            // it doesn't know which reminders were registered against the grain
            // hence, this activation may receive a reminder that it didn't register itself, but
            // the previous activation (incarnation of the grain) registered... so, play it safe
            if (!this.sequence.ContainsKey(reminderName))
            {
                this.sequence.Add(reminderName, 0); // we'll get upto date to the latest sequence number while processing this tick
            }

            allReminders[reminderName].Fired.Add(status.CurrentTickTime);

            // calculating tick sequence number

            // we do all arithmetics on DateTime by converting into long because we dont have divide operation on DateTime
            // using dateTime.Ticks is not accurate as between two invocations of ReceiveReminder(), there maybe < period.Ticks
            // if # of ticks between two consecutive ReceiveReminder() is larger than period.Ticks, everything is fine... the problem is when its less
            // thus, we reduce our accuracy by ACCURACY ... here, we are preparing all used variables for the given accuracy
            long now = status.CurrentTickTime.Ticks / aCCURACY; //DateTime.UtcNow.Ticks / ACCURACY;
            long first = status.FirstTickTime.Ticks / aCCURACY;
            long per = status.Period.Ticks / aCCURACY;
            long sequenceNumber = 1 + ((now - first) / per);

            // end of calculating tick sequence number

            // do switch-ing here
            if (sequenceNumber < this.sequence[reminderName])
            {
                this.logger.LogInformation("ReceiveReminder: {Reminder} Incorrect tick {ExpectedSequenceNumber} vs. {SequenceNumber} with status {Status}.", reminderName, this.sequence[reminderName], sequenceNumber, status);
                return Task.CompletedTask;
            }
            this.sequence[reminderName] = sequenceNumber;
            this.logger.LogInformation("ReceiveReminder: {ReminderNAme} Sequence # {SequenceNumber} with status {Status}.", reminderName, this.sequence[reminderName], status);

            string fileName = GetFileName(reminderName);
            string counterValue = this.sequence[reminderName].ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(fileName, counterValue);

            return Task.CompletedTask;
        }

        public async Task StopReminder(string reminderName)
        {
            this.logger.LogInformation("Stopping reminder {ReminderName}.", reminderName);
            // we dont reset counter as we want the test methods to be able to read it even after stopping the reminder
            //return UnregisterReminder(allReminders[reminderName]);
            ReminderState state;
            if (this.allReminders.TryGetValue(reminderName, out state))
            {
                await this.UnregisterReminder(state.Reminder);
                state.Log.Add((DateTime.UtcNow, $"Unregistering {reminderName}"));
                allReminders[reminderName] = state with { Unregistered = DateTime.UtcNow };
            }
            else
            {
                // during failures, there may be reminders registered by an earlier activation that we dont have cached locally
                // therefore, we need to update our local cache
                await GetMissingReminders();
                if (this.allReminders.TryGetValue(reminderName, out state))
                {
                    await this.UnregisterReminder(state.Reminder);
                    state.Log.Add((DateTime.UtcNow, $"Unregistering {reminderName}"));
                    allReminders[reminderName] = state with { Unregistered = DateTime.UtcNow };
                }
                else
                {
                    //var reminders = await this.GetRemindersList();
                    throw new OrleansException(string.Format(
                        "Could not find reminder {0} in grain {1}", reminderName, this.IdentityString));
                }
            }
        }

        private async Task GetMissingReminders()
        {
            List<IGrainReminder> reminders = await this.GetReminders();
            this.logger.LogInformation("Got missing reminders {Reminders}", Utils.EnumerableToString(reminders));
            foreach (IGrainReminder l in reminders)
            {
                if (!this.allReminders.ContainsKey(l.ReminderName))
                {
                    this.allReminders.Add(l.ReminderName, new ReminderState(Reminder: l) { Registered = DateTime.UtcNow, Log = { (DateTime.UtcNow, $"Adding missing: {l.ReminderName}") } });
                }
            }
        }

        public async Task StopReminder(IGrainReminder reminder)
        {
            this.logger.LogInformation("Stopping reminder (using ref) {Reminder}.", reminder);
            // we dont reset counter as we want the test methods to be able to read it even after stopping the reminder
            await this.UnregisterReminder(reminder);
            var state = allReminders[reminder.ReminderName];
            state.Log.Add((DateTime.UtcNow, $"Stopping {reminder.ReminderName}"));
            allReminders[reminder.ReminderName] = state with { Unregistered = DateTime.UtcNow };
        }

        public Task<TimeSpan> GetReminderPeriod(string reminderName)
        {
            return Task.FromResult(this.period);
        }

        public Task<(TimeSpan DueTime, TimeSpan Period)> GetReminderDueTimeAndPeriod(string reminderName)
        {
            return Task.FromResult((this.period - TimeSpan.FromSeconds(2), this.period));
        }

        public Task<long> GetCounter(string name)
        {
            string fileName = GetFileName(name);
            string data = File.ReadAllText(fileName);
            long counterValue = long.Parse(data);
            return Task.FromResult(counterValue);
        }

        public Task<IGrainReminder> GetReminderObject(string reminderName)
        {
            return this.GetReminder(reminderName);
        }

        public async Task<List<IGrainReminder>> GetRemindersList()
        {
            return await this.GetReminders();
        }

        private string GetFileName(string reminderName)
        {
            return string.Format("{0}{1}", this.filePrefix, reminderName);
        }

        public static TimeSpan GetDefaultPeriod(ILogger log)
        {
            int period = 12; // Seconds
            var reminderPeriod = TimeSpan.FromSeconds(period);
            log.LogInformation("Using reminder period of {Period} in ReminderTestGrain", reminderPeriod);
            return reminderPeriod;
        }

        public async Task EraseReminderTable()
        {
            await this.reminderTable.TestOnlyClearTable();
        }
    }

    // NOTE: do not make changes here ... this is a copy of ReminderTestGrain
    // changes to make when copying:
    //      1. rename logger to ReminderCopyGrain
    //      2. filePrefix should start with "gc", instead of "g"
    public class ReminderTestCopyGrain : Grain, IReminderTestCopyGrain, IRemindable
    {
        private readonly IReminderRegistry unvalidatedReminderRegistry;
        Dictionary<string, IGrainReminder> allReminders;
        Dictionary<string, long> sequence;
        private TimeSpan period;

        private static long aCCURACY = 50 * TimeSpan.TicksPerMillisecond; // when we use ticks to compute sequence numbers, we might get wrong results as timeouts don't happen with precision of ticks  ... we keep this as a leeway

        private ILogger logger;
        private long myId; // used to distinguish during debugging between multiple activations of the same grain

        private string filePrefix;

        public ReminderTestCopyGrain(IServiceProvider services, ILoggerFactory loggerFactory)
        {
            this.unvalidatedReminderRegistry = new UnvalidatedReminderRegistry(services);
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.myId = new Random().Next();
            this.allReminders = new Dictionary<string, IGrainReminder>();
            this.sequence = new Dictionary<string, long>();
            this.period = ReminderTestGrain2.GetDefaultPeriod(this.logger);
            this.logger.LogInformation("OnActivateAsync.");
            this.filePrefix = "gc" + this.GrainId.Key + "_";
            await GetMissingReminders();
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            this.logger.LogInformation("OnDeactivateAsync.");
            return Task.CompletedTask;
        }

        public async Task<IGrainReminder> StartReminder(string reminderName, TimeSpan? p = null, bool validate = false)
        {
            TimeSpan usePeriod = p ?? this.period;
            this.logger.LogInformation("Starting reminder {ReminderName} for {GrainId}", reminderName, this.GrainId);
            IGrainReminder r;
            if (validate)
                r = await this.RegisterOrUpdateReminder(reminderName, usePeriod - TimeSpan.FromSeconds(2), usePeriod);
            else
                r = await this.unvalidatedReminderRegistry.RegisterOrUpdateReminder(
                    this.GrainId,
                    reminderName,
                    usePeriod - TimeSpan.FromSeconds(2),
                    usePeriod);
            if (this.allReminders.ContainsKey(reminderName))
            {
                this.allReminders[reminderName] = r;
                this.sequence[reminderName] = 0;
            }
            else
            {
                this.allReminders.Add(reminderName, r);
                this.sequence.Add(reminderName, 0);
            }

            File.Delete(GetFileName(reminderName)); // if successfully started, then remove any old data
            this.logger.LogInformation("Started reminder {Reminder}.", r);
            return r;
        }

        public Task ReceiveReminder(string reminderName, TickStatus status)
        {
            // it can happen that due to failure, when a new activation is created,
            // it doesn't know which reminders were registered against the grain
            // hence, this activation may receive a reminder that it didn't register itself, but
            // the previous activation (incarnation of the grain) registered... so, play it safe
            if (!this.sequence.ContainsKey(reminderName))
            {
                // allReminders.Add(reminderName, r); // not using allReminders at the moment
                //counters.Add(reminderName, 0);
                this.sequence.Add(reminderName, 0); // we'll get upto date to the latest sequence number while processing this tick
            }

            // calculating tick sequence number

            // we do all arithmetics on DateTime by converting into long because we dont have divide operation on DateTime
            // using dateTime.Ticks is not accurate as between two invocations of ReceiveReminder(), there maybe < period.Ticks
            // if # of ticks between two consecutive ReceiveReminder() is larger than period.Ticks, everything is fine... the problem is when its less
            // thus, we reduce our accuracy by ACCURACY ... here, we are preparing all used variables for the given accuracy
            long now = status.CurrentTickTime.Ticks / aCCURACY; //DateTime.UtcNow.Ticks / ACCURACY;
            long first = status.FirstTickTime.Ticks / aCCURACY;
            long per = status.Period.Ticks / aCCURACY;
            long sequenceNumber = 1 + ((now - first) / per);

            // end of calculating tick sequence number

            // do switch-ing here
            if (sequenceNumber < this.sequence[reminderName])
            {
                this.logger.LogInformation("{ReminderName} Incorrect tick {ExpectedSequenceNumber} vs. {SequenceNumber} with status {Status}.", reminderName, this.sequence[reminderName], sequenceNumber, status);
                return Task.CompletedTask;
            }

            this.sequence[reminderName] = sequenceNumber;
            this.logger.LogInformation("{ReminderName} Sequence # {SequenceNumber} with status {Status}.", reminderName, this.sequence[reminderName], status);

            File.WriteAllText(GetFileName(reminderName), this.sequence[reminderName].ToString());

            return Task.CompletedTask;
        }

        public async Task StopReminder(string reminderName)
        {
            this.logger.LogInformation("Stopping reminder {Reminder}.", reminderName);
            // we dont reset counter as we want the test methods to be able to read it even after stopping the reminder
            //return UnregisterReminder(allReminders[reminderName]);
            IGrainReminder reminder;
            if (this.allReminders.TryGetValue(reminderName, out reminder))
            {
                await this.UnregisterReminder(reminder);
            }
            else
            {
                // during failures, there may be reminders registered by an earlier activation that we dont have cached locally
                // therefore, we need to update our local cache
                await GetMissingReminders();
                await this.UnregisterReminder(this.allReminders[reminderName]);
            }
        }

        private async Task GetMissingReminders()
        {
            List<IGrainReminder> reminders = await this.GetReminders();
            foreach (IGrainReminder l in reminders)
            {
                if (!this.allReminders.ContainsKey(l.ReminderName))
                {
                    this.allReminders.Add(l.ReminderName, l);
                }
            }
        }

        public async Task StopReminder(IGrainReminder reminder)
        {
            this.logger.LogInformation("Stopping reminder (using ref) {Reminder}.", reminder);
            // we dont reset counter as we want the test methods to be able to read it even after stopping the reminder
            await this.UnregisterReminder(reminder);
        }

        public Task<TimeSpan> GetReminderPeriod(string reminderName)
        {
            return Task.FromResult(this.period);
        }

        public Task<long> GetCounter(string name)
        {
            return Task.FromResult(long.Parse(File.ReadAllText(GetFileName(name))));
        }

        public async Task<IGrainReminder> GetReminderObject(string reminderName)
        {
            return await this.GetReminder(reminderName);
        }
        public async Task<List<IGrainReminder>> GetRemindersList()
        {
            return await this.GetReminders();
        }

        private string GetFileName(string reminderName)
        {
            return string.Format("{0}{1}", this.filePrefix, reminderName);
        }
    }

    public class WrongReminderGrain : Grain, IReminderGrainWrong
    {
        private ILogger logger;

        public WrongReminderGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation("OnActivateAsync.");
            return Task.CompletedTask;
        }

        public async Task<bool> StartReminder(string reminderName)
        {
            this.logger.LogInformation("Starting reminder {Reminder}.", reminderName);
            IGrainReminder r = await this.RegisterOrUpdateReminder(reminderName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            this.logger.LogInformation("Started reminder {Reminder}. It shouldn't have succeeded!", r);
            return true;
        }
    }


    internal sealed class UnvalidatedReminderRegistry : GrainServiceClient<IReminderService>, IReminderRegistry
    {
        public UnvalidatedReminderRegistry(IServiceProvider serviceProvider) : base(serviceProvider) { }

        public Task<IGrainReminder> RegisterOrUpdateReminder(GrainId callingGrainId, string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            return GetGrainService(callingGrainId).RegisterOrUpdateReminder(callingGrainId, reminderName, dueTime, period);
        }

        public Task UnregisterReminder(GrainId callingGrainId, IGrainReminder reminder)
            => GetGrainService(callingGrainId).UnregisterReminder(reminder);

        public Task<IGrainReminder> GetReminder(GrainId callingGrainId, string reminderName)
        {
            return GetGrainService(callingGrainId).GetReminder(callingGrainId, reminderName);
        }

        public Task<List<IGrainReminder>> GetReminders(GrainId callingGrainId)
        {
            return GetGrainService(callingGrainId).GetReminders(callingGrainId);
        }
    }
}
#pragma warning restore 612, 618