using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Services;
using Orleans.Timers;

namespace Orleans.Runtime.ReminderService
{
    internal class ReminderRegistry : GrainServiceClient<IReminderService>, IReminderRegistry
    {
        private const string ReminderServiceNotConfigured =
           "The reminder service has not been configured. Reminders can be configured using extension methods from the following packages:"
           + "\n  * Microsoft.Orleans.Reminders.AzureStorage via ISiloBuilder.UseAzureTableReminderService(...)"
           + "\n  * Microsoft.Orleans.Reminders.AdoNet via ISiloBuilder.UseAdoNetReminderService(...)"
           + "\n  * Microsoft.Orleans.Reminders.DynamoDB via via ISiloBuilder.UseDynamoDBReminderService(...)"
           + "\n  * Microsoft.Orleans.OrleansRuntime via ISiloBuilder.UseInMemoryReminderService(...) (Note: for development purposes only)"
           + "\n  * Others, see: https://www.nuget.org/packages?q=Microsoft.Orleans.Reminders.";

        private const uint MaxSupportedTimeout = 0xfffffffe;
        private readonly IGrainFactory grainFactory;
        private readonly IServiceProvider serviceProvider;
        private bool reminderServiceRegistered;

        public ReminderRegistry(IGrainFactory grainFactory, IServiceProvider serviceProvider) : base(serviceProvider)
        {
            this.grainFactory = grainFactory;
            this.serviceProvider = serviceProvider;
        }

        public Task<IGrainReminder> RegisterOrUpdateReminder(GrainId callingGrainId, string reminderName, TimeSpan dueTime, TimeSpan period)
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

            var callingGrainReference = grainFactory.GetGrain(callingGrainId).AsReference();
            return GetGrainService(callingGrainId).RegisterOrUpdateReminder(callingGrainReference, reminderName, dueTime, period);
        }

        public Task UnregisterReminder(GrainId callingGrainId, IGrainReminder reminder)
        {
            this.EnsureReminderServiceRegistered();
            return GetGrainService(callingGrainId).UnregisterReminder(reminder);
        }

        public Task<IGrainReminder> GetReminder(GrainId callingGrainId, string reminderName)
        {
            this.EnsureReminderServiceRegistered();
            if (string.IsNullOrEmpty(reminderName))
            {
                throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));
            }

            var callingGrainReference = grainFactory.GetGrain(callingGrainId).AsReference();
            return GetGrainService(callingGrainId).GetReminder(callingGrainReference, reminderName);
        }

        public Task<List<IGrainReminder>> GetReminders(GrainId callingGrainId)
        {
            this.EnsureReminderServiceRegistered();
            var callingGrainReference = grainFactory.GetGrain(callingGrainId).AsReference();
            return GetGrainService(callingGrainId).GetReminders(callingGrainReference);
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