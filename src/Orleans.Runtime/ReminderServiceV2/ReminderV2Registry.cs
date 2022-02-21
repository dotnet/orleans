using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Services;
using Orleans.Timers;

namespace Orleans.Runtime.ReminderServiceV2
{
    internal class ReminderV2Registry : GrainServiceClient<IReminderServiceV2>, IReminderV2Registry
    {
        private const string ReminderServiceNotConfigured =
           "The reminder service has not been configured. Reminders can be configured using extension methods from the following packages:"
           + "\n  * Microsoft.Orleans.Reminders.AzureStorage via ISiloBuilder.UseAzureTableReminderService(...)"
           + "\n  * Microsoft.Orleans.Reminders.AdoNet via ISiloBuilder.UseAdoNetReminderService(...)"
           + "\n  * Microsoft.Orleans.Reminders.DynamoDB via via ISiloBuilder.UseDynamoDBReminderService(...)"
           + "\n  * Microsoft.Orleans.OrleansRuntime via ISiloBuilder.UseInMemoryReminderService(...) (Note: for development purposes only)"
           + "\n  * Others, see: https://www.nuget.org/packages?q=Microsoft.Orleans.Reminders.";

        private const uint MaxSupportedTimeout = 0xfffffffe;
        private readonly IServiceProvider serviceProvider;
        private bool reminderServiceRegistered;

        public ReminderV2Registry(IServiceProvider serviceProvider) : base(serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public Task<IGrainReminderV2> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            this.EnsureReminderServiceRegistered();

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

            var reminderOptions = serviceProvider.GetService<IOptions<ReminderOptions>>();
            var minReminderPeriod = reminderOptions.Value.MinimumReminderPeriod;

            if (period < minReminderPeriod)
            {
                var msg =
                    string.Format(
                        "Cannot register reminder {0} as requested period ({1}) is less than minimum allowed reminder period ({2})",
                        reminderName,
                        period,
                        minReminderPeriod);
                throw new ArgumentException(msg);
            }
            if (string.IsNullOrEmpty(reminderName))
            {
                throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));
            }

            return GrainService.RegisterOrUpdateReminder(CallingGrainReference, reminderName, dueTime, period);
        }

        public Task UnregisterReminder(IGrainReminderV2 reminder)
        {
            this.EnsureReminderServiceRegistered();
            return GrainService.UnregisterReminder(reminder);
        }

        public Task<IGrainReminderV2> GetReminder(string reminderName)
        {
            this.EnsureReminderServiceRegistered();
            if (string.IsNullOrEmpty(reminderName))
            {
                throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));
            }

            return GrainService.GetReminder(CallingGrainReference, reminderName);
        }

        public Task<List<IGrainReminderV2>> GetReminders()
        {
            this.EnsureReminderServiceRegistered();
            return GrainService.GetReminders(CallingGrainReference);
        }

        private void EnsureReminderServiceRegistered()
        {
            if (this.reminderServiceRegistered) return;
            var reminderTable = this.serviceProvider.GetService<IReminderTable>();
            if (reminderTable == null)
            {
                throw new OrleansConfigurationException(ReminderServiceNotConfigured);
            }

            this.reminderServiceRegistered = true;
        }
    }
}