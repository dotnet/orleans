using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Services;
using Orleans.Timers;

namespace Orleans.Runtime.ReminderService
{
    internal class ReminderRegistry : GrainServiceClient<IReminderService>, IReminderRegistry
    {
        private const uint MaxSupportedTimeout = 0xfffffffe;

        public ReminderRegistry(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        public Task<IGrainReminder> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            // Perform input volatility checks that are consistent with System.Threading.Timer
            // http://referencesource.microsoft.com/#mscorlib/system/threading/timer.cs,c454f2afe745d4d3,references
            var dueTm = (long) dueTime.TotalMilliseconds;
            if (dueTm < -1)
                throw new ArgumentOutOfRangeException(nameof(dueTime), "Cannot use negative dueTime to create a reminder");
            if (dueTm > MaxSupportedTimeout)
                throw new ArgumentOutOfRangeException(
                    nameof(dueTime),
                    $"Cannot use value larger than {MaxSupportedTimeout}ms for dueTime when creating a reminder");

            var periodTm = (long) period.TotalMilliseconds;
            if (periodTm < -1)
                throw new ArgumentOutOfRangeException(nameof(period), "Cannot use negative period to create a reminder");
            if (periodTm > MaxSupportedTimeout)
                throw new ArgumentOutOfRangeException(
                    nameof(period),
                    $"Cannot use value larger than {MaxSupportedTimeout}ms for period when creating a reminder");

            if (period < Constants.MinReminderPeriod)
            {
                var msg =
                    string.Format(
                        "Cannot register reminder {0} as requested period ({1}) is less than minimum allowed reminder period ({2})",
                        reminderName,
                        period,
                        Constants.MinReminderPeriod);
                throw new ArgumentException(msg);
            }
            if (string.IsNullOrEmpty(reminderName))
            {
                throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));
            }

            return GrainService.RegisterOrUpdateReminder(CallingGrainReference, reminderName, dueTime, period);
        }

        public Task UnregisterReminder(IGrainReminder reminder)
        {
            return GrainService.UnregisterReminder(reminder);
        }

        public Task<IGrainReminder> GetReminder(string reminderName)
        {
            if (string.IsNullOrEmpty(reminderName))
            {
                throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));
            }

            return GrainService.GetReminder(CallingGrainReference, reminderName);
        }

        public Task<List<IGrainReminder>> GetReminders()
        {
            return GrainService.GetReminders(CallingGrainReference);
        }
    }
}