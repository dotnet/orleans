using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Timers
{
    internal class ReminderRegistry : IReminderRegistry
    {
        public Task<IGrainReminder> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            if (period < Constants.MinReminderPeriod)
            {
                string msg = string.Format("Cannot register reminder {0} as requested period ({1}) is less than minimum allowed reminder period ({2})", reminderName, period, Constants.MinReminderPeriod);
                throw new ArgumentException(msg);
            }
            return RuntimeClient.Current.RegisterOrUpdateReminder(reminderName, dueTime, period);
        }

        public Task UnregisterReminder(IGrainReminder reminder)
        {
            return RuntimeClient.Current.UnregisterReminder(reminder);
        }

        public Task<IGrainReminder> GetReminder(string reminderName)
        {
            return RuntimeClient.Current.GetReminder(reminderName);
        }

        public Task<List<IGrainReminder>> GetReminders()
        {
            return RuntimeClient.Current.GetReminders();
        }
    }
}
