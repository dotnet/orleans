using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans;
using Orleans.Runtime;

namespace LoadTestGrains
{
    public class ReminderLoadTestGrain : Grain, IReminderLoadTestGrain
    {
        // This value needs to be long enough to guarantee that a reminder will never tick. This serves to isolate reminder tick processing from registration, which is what we want to test.
        private static readonly TimeSpan ReminderPeriod = TimeSpan.FromHours(24);
        
        private Logger _logger;
        private Dictionary<string, IGrainReminder> _reminders;

        public override Task OnActivateAsync()
        {
            _logger = GetLogger("ReminderLoadTestGrain " + base.RuntimeIdentity);
            _reminders = new Dictionary<string, IGrainReminder>();

            return TaskDone.Done;
        }

        public Task ReceiveReminder(string reminderName, TickStatus status)
        {
            return TaskDone.Done;
        }

        public Task Noop()
        {
            return TaskDone.Done;
        }

        public async Task RegisterReminder(string reminderName)
        {
            IGrainReminder handle = await RegisterOrUpdateReminder(reminderName, ReminderPeriod, ReminderPeriod);
            _reminders.Add(reminderName, handle);
        }
        public Task UnregisterReminder(string reminderName)
        {
            IGrainReminder handle = _reminders[reminderName];
            return UnregisterReminder(handle);
        }
        
    }
}
