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
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Services;
using Orleans.Timers;
using UnitTests.GrainInterfaces;


#pragma warning disable 612,618
namespace UnitTests.Grains
{
    // NOTE: if you make any changes here, copy them to ReminderTestCopyGrain
    public class ReminderV2TestGrain2 : Grain, IReminderV2TestGrain2, IRemindable
    {
        private readonly IReminderV2Table reminderTable;

        private readonly IReminderV2Registry unvalidatedReminderRegistry;
        Dictionary<string, IGrainReminderV2> allReminders;
        Dictionary<string, long> sequence;
        private TimeSpan period;

        private static long aCCURACY = 50 * TimeSpan.TicksPerMillisecond; // when we use ticks to compute sequence numbers, we might get wrong results as timeouts don't happen with precision of ticks  ... we keep this as a leeway

        private IOptions<ReminderOptions> reminderOptions;

        private ILogger logger;
        private string _id; // used to distinguish during debugging between multiple activations of the same grain

        private string filePrefix;

        public ReminderV2TestGrain2(IServiceProvider services, IReminderV2Table reminderTable, ILoggerFactory loggerFactory)
        {
            this.reminderTable = reminderTable;
            this.unvalidatedReminderRegistry = new UnvalidatedReminderV2Registry(services);
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
            this.reminderOptions = services.GetService<IOptions<ReminderOptions>>();
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this._id = Guid.NewGuid().ToString();
            this.allReminders = new Dictionary<string, IGrainReminderV2>();
            this.sequence = new Dictionary<string, long>();
            this.period = GetDefaultPeriod(this.logger);
            this.logger.Info("OnActivateAsync.");
            this.filePrefix = "g" + this.GrainId.ToString().Replace('/', '_') + "_";
            return GetMissingReminders();
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            this.logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public async Task<IGrainReminderV2> StartReminder(string reminderName, TimeSpan? p = null, bool validate = false)
        {
            TimeSpan usePeriod = p ?? this.period;
            this.logger.Info("Starting reminder {0}.", reminderName);
            TimeSpan dueTime;
            if (reminderOptions.Value.MinimumReminderPeriod < TimeSpan.FromSeconds(2))
                dueTime = TimeSpan.FromSeconds(2) - reminderOptions.Value.MinimumReminderPeriod;
            else dueTime = usePeriod - TimeSpan.FromSeconds(2);

            IGrainReminderV2 r;
            if (validate)
                r = await RegisterOrUpdateReminderV2(reminderName, dueTime, usePeriod);
            else
                r = await this.unvalidatedReminderRegistry.RegisterOrUpdateReminder(reminderName, dueTime, usePeriod);

            this.allReminders[reminderName] = r;
            this.sequence[reminderName] = 0;

            string fileName = GetFileName(reminderName);
            File.Delete(fileName); // if successfully started, then remove any old data
            this.logger.Info("Started reminder {0}", r);
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
                this.logger.Info("ReceiveReminder: {0} Incorrect tick {1} vs. {2} with status {3}.", reminderName, this.sequence[reminderName], sequenceNumber, status);
                return Task.CompletedTask;
            }

            this.sequence[reminderName] = sequenceNumber;
            this.logger.Info("ReceiveReminder: {0} Sequence # {1} with status {2}.", reminderName, this.sequence[reminderName], status);

            string fileName = GetFileName(reminderName);
            string counterValue = this.sequence[reminderName].ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(fileName, counterValue);

            return Task.CompletedTask;
        }

        public async Task StopReminder(string reminderName)
        {
            this.logger.Info("Stopping reminder {0}.", reminderName);
            // we dont reset counter as we want the test methods to be able to read it even after stopping the reminder
            //return UnregisterReminder(allReminders[reminderName]);
            IGrainReminderV2 reminder;
            if (this.allReminders.TryGetValue(reminderName, out reminder))
            {
                await UnregisterReminder(reminder);
            }
            else
            {
                // during failures, there may be reminders registered by an earlier activation that we dont have cached locally
                // therefore, we need to update our local cache
                await GetMissingReminders();
                if (this.allReminders.TryGetValue(reminderName, out reminder))
                {
                    await UnregisterReminder(reminder);
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
            List<IGrainReminderV2> reminders = await base.GetRemindersV2();
            this.logger.Info("Got missing reminders {0}", Utils.EnumerableToString(reminders));
            foreach (IGrainReminderV2 l in reminders)
            {
                if (!this.allReminders.ContainsKey(l.ReminderName))
                {
                    this.allReminders.Add(l.ReminderName, l);
                }
            }
        }


        public async Task StopReminder(IGrainReminderV2 reminder)
        {
            this.logger.Info("Stopping reminder (using ref) {0}.", reminder);
            // we dont reset counter as we want the test methods to be able to read it even after stopping the reminder
            await UnregisterReminder(reminder);
        }

        public Task<TimeSpan> GetReminderPeriod(string reminderName)
        {
            return Task.FromResult(this.period);
        }

        public Task<long> GetCounter(string name)
        {
            string fileName = GetFileName(name);
            string data = File.ReadAllText(fileName);
            long counterValue = long.Parse(data);
            return Task.FromResult(counterValue);
        }

        public Task<IGrainReminderV2> GetReminderObject(string reminderName)
        {
            return base.GetReminderV2(reminderName);
        }
        public async Task<List<IGrainReminderV2>> GetRemindersList()
        {
            return await base.GetRemindersV2();
        }

        private string GetFileName(string reminderName)
        {
            return string.Format("{0}{1}", this.filePrefix, reminderName);
        }

        public static TimeSpan GetDefaultPeriod(ILogger log)
        {
            int period = 12; // Seconds
            var reminderPeriod = TimeSpan.FromSeconds(period);
            log.Info("Using reminder period of {0} in ReminderTestGrain", reminderPeriod);
            return reminderPeriod;
        }

        public async Task EraseReminderTable()
        {
            await this.reminderTable.TestOnlyClearTable();
        }
    }

    // NOTE: do not make changes here ... this is a copy of ReminderV2TestGrain
    // changes to make when copying:
    //      1. rename logger to ReminderCopyGrain
    //      2. filePrefix should start with "gc", instead of "g"
    public class ReminderV2TestCopyGrain : Grain, IReminderV2TestCopyGrain, IRemindable
    {
        private readonly IReminderV2Registry unvalidatedReminderRegistry;
        Dictionary<string, IGrainReminderV2> allReminders;
        Dictionary<string, long> sequence;
        private TimeSpan period;

        private static long aCCURACY = 50 * TimeSpan.TicksPerMillisecond; // when we use ticks to compute sequence numbers, we might get wrong results as timeouts don't happen with precision of ticks  ... we keep this as a leeway

        private ILogger logger;
        private long myId; // used to distinguish during debugging between multiple activations of the same grain

        private string filePrefix;

        public ReminderV2TestCopyGrain(IServiceProvider services, ILoggerFactory loggerFactory)
        {
            this.unvalidatedReminderRegistry = new UnvalidatedReminderV2Registry(services); ;
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.myId = new Random().Next();
            this.allReminders = new Dictionary<string, IGrainReminderV2>();
            this.sequence = new Dictionary<string, long>();
            this.period = ReminderTestGrain2.GetDefaultPeriod(this.logger);
            this.logger.Info("OnActivateAsync.");
            this.filePrefix = "gc" + this.GrainId.Key + "_";
            await GetMissingReminders();
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            this.logger.Info("OnDeactivateAsync.");
            return Task.CompletedTask;
        }

        public async Task<IGrainReminderV2> StartReminder(string reminderName, TimeSpan? p = null, bool validate = false)
        {
            TimeSpan usePeriod = p ?? this.period;
            this.logger.Info("Starting reminder {0} for {1}", reminderName, this.GrainId);
            IGrainReminderV2 r;
            if (validate)
                r = await RegisterOrUpdateReminderV2(reminderName, /*TimeSpan.FromSeconds(3)*/usePeriod - TimeSpan.FromSeconds(2), usePeriod);
            else
                r = await this.unvalidatedReminderRegistry.RegisterOrUpdateReminder(
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
            this.logger.Info("Started reminder {0}.", r);
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
                this.logger.Info("{0} Incorrect tick {1} vs. {2} with status {3}.", reminderName, this.sequence[reminderName], sequenceNumber, status);
                return Task.CompletedTask;
            }

            this.sequence[reminderName] = sequenceNumber;
            this.logger.Info("{0} Sequence # {1} with status {2}.", reminderName, this.sequence[reminderName], status);

            File.WriteAllText(GetFileName(reminderName), this.sequence[reminderName].ToString());

            return Task.CompletedTask;
        }

        public async Task StopReminder(string reminderName)
        {
            this.logger.Info("Stopping reminder {0}.", reminderName);
            // we dont reset counter as we want the test methods to be able to read it even after stopping the reminder
            //return UnregisterReminder(allReminders[reminderName]);
            IGrainReminderV2 reminder;
            if (this.allReminders.TryGetValue(reminderName, out reminder))
            {
                await UnregisterReminder(reminder);
            }
            else
            {
                // during failures, there may be reminders registered by an earlier activation that we dont have cached locally
                // therefore, we need to update our local cache
                await GetMissingReminders();
                await UnregisterReminder(this.allReminders[reminderName]);
            }
        }

        private async Task GetMissingReminders()
        {
            List<IGrainReminderV2> reminders = await base.GetRemindersV2();
            foreach (IGrainReminderV2 l in reminders)
            {
                if (!this.allReminders.ContainsKey(l.ReminderName))
                {
                    this.allReminders.Add(l.ReminderName, l);
                }
            }
        }

        public async Task StopReminder(IGrainReminderV2 reminder)
        {
            this.logger.Info("Stopping reminder (using ref) {0}.", reminder);
            // we dont reset counter as we want the test methods to be able to read it even after stopping the reminder
            await UnregisterReminder(reminder);
        }

        public Task<TimeSpan> GetReminderPeriod(string reminderName)
        {
            return Task.FromResult(this.period);
        }

        public Task<long> GetCounter(string name)
        {
            return Task.FromResult(long.Parse(File.ReadAllText(GetFileName(name))));
        }

        public async Task<IGrainReminderV2> GetReminderObject(string reminderName)
        {
            return await base.GetReminderV2(reminderName);
        }
        public async Task<List<IGrainReminderV2>> GetRemindersList()
        {
            return await base.GetRemindersV2();
        }

        private string GetFileName(string reminderName)
        {
            return string.Format("{0}{1}", this.filePrefix, reminderName);
        }
    }

    public class WrongReminderV2Grain : Grain, IReminderV2GrainWrong
    {
        private ILogger logger;

        public WrongReminderV2Grain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger.Info("OnActivateAsync.");
            return Task.CompletedTask;
        }

        public async Task<bool> StartReminder(string reminderName)
        {
            this.logger.Info("Starting reminder {0}.", reminderName);
            IGrainReminderV2 r = await RegisterOrUpdateReminderV2(reminderName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            this.logger.Info("Started reminder {0}. It shouldn't have succeeded!", r);
            return true;
        }
    }


    internal class UnvalidatedReminderV2Registry : GrainServiceClient<IReminderServiceV2>, IReminderV2Registry
    {
        public UnvalidatedReminderV2Registry(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        public Task<IGrainReminderV2> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            return this.GrainService.RegisterOrUpdateReminder(this.CallingGrainReference, reminderName, dueTime, period);
        }

        public Task UnregisterReminder(IGrainReminderV2 reminder)
        {
            return this.GrainService.UnregisterReminder(reminder);
        }

        public Task<IGrainReminderV2> GetReminder(string reminderName)
        {
            return this.GrainService.GetReminder(this.CallingGrainReference, reminderName);
        }

        public Task<List<IGrainReminderV2>> GetReminders()
        {
            return this.GrainService.GetReminders(this.CallingGrainReference);
        }
    }
}
#pragma warning restore 612,618