using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime.Services;
using Orleans.Timers;

#nullable enable
namespace Orleans.Runtime.ReminderService
{
    internal sealed class ReminderRegistry : GrainServiceClient<IReminderService>, IReminderRegistry
    {
        private const uint MaxSupportedTimeout = 0xfffffffe;
        private IServiceProvider? serviceProvider;
        private readonly ReminderOptions options;

        public ReminderRegistry(IServiceProvider serviceProvider, IOptions<ReminderOptions> options) : base(serviceProvider)
        {
            this.serviceProvider = serviceProvider;
            this.options = options.Value;
        }

        public Task<IGrainReminder> RegisterOrUpdateReminder(GrainId callingGrainId, string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            // Perform input volatility checks that are consistent with System.Threading.Timer
            // http://referencesource.microsoft.com/#mscorlib/system/threading/timer.cs,c454f2afe745d4d3,references
            if (dueTime.Ticks < 0 && dueTime != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(dueTime), "Cannot use negative dueTime to create a reminder");
            if (dueTime.Ticks > MaxSupportedTimeout * TimeSpan.TicksPerMillisecond)
                throw new ArgumentOutOfRangeException(nameof(dueTime), $"Cannot use value larger than {MaxSupportedTimeout}ms for dueTime when creating a reminder");

            if (period.Ticks < 0 && period != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(period), "Cannot use negative period to create a reminder");
            if (period.Ticks > MaxSupportedTimeout * TimeSpan.TicksPerMillisecond)
                throw new ArgumentOutOfRangeException(nameof(period), $"Cannot use value larger than {MaxSupportedTimeout}ms for period when creating a reminder");

            var minReminderPeriod = options.MinimumReminderPeriod;
            if (period < minReminderPeriod)
                throw new ArgumentException($"Cannot register reminder {reminderName} as requested period ({period}) is less than minimum allowed reminder period ({minReminderPeriod})");

            if (string.IsNullOrEmpty(reminderName))
                throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));

            EnsureReminderServiceRegisteredAndInGrainContext();
            return GetGrainService(callingGrainId).RegisterOrUpdateReminder(callingGrainId, reminderName, dueTime, period);
        }

        public Task UnregisterReminder(GrainId callingGrainId, IGrainReminder reminder)
        {
            EnsureReminderServiceRegisteredAndInGrainContext();
            return GetGrainService(callingGrainId).UnregisterReminder(reminder);
        }

        public Task<IGrainReminder> GetReminder(GrainId callingGrainId, string reminderName)
        {
            if (string.IsNullOrEmpty(reminderName))
                throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));

            EnsureReminderServiceRegisteredAndInGrainContext();
            return GetGrainService(callingGrainId).GetReminder(callingGrainId, reminderName);
        }

        public Task<List<IGrainReminder>> GetReminders(GrainId callingGrainId)
        {
            EnsureReminderServiceRegisteredAndInGrainContext();
            return GetGrainService(callingGrainId).GetReminders(callingGrainId);
        }

        private void EnsureReminderServiceRegisteredAndInGrainContext()
        {
            if (RuntimeContext.Current is null) ThrowInvalidContext();
            if (serviceProvider != null) ValidateServiceProvider();
        }

        private void ValidateServiceProvider()
        {
            if (serviceProvider is { } sp && sp.GetService<IReminderTable>() is null)
            {
                throw new OrleansConfigurationException(
                    "The reminder service has not been configured. Reminders can be configured using extension methods from the following packages:"
                    + "\n  * Microsoft.Orleans.Reminders.AzureStorage via ISiloBuilder.UseAzureTableReminderService(...)"
                    + "\n  * Microsoft.Orleans.Reminders.AdoNet via ISiloBuilder.UseAdoNetReminderService(...)"
                    + "\n  * Microsoft.Orleans.Reminders.DynamoDB via via ISiloBuilder.UseDynamoDBReminderService(...)"
                    + "\n  * Microsoft.Orleans.OrleansRuntime via ISiloBuilder.UseInMemoryReminderService(...) (Note: for development purposes only)"
                    + "\n  * Others, see: https://www.nuget.org/packages?q=Microsoft.Orleans.Reminders.");
            }

            serviceProvider = null;
        }

        private static void ThrowInvalidContext()
        {
            throw new InvalidOperationException("Attempted to access grain from a non-grain context, such as a background thread, which is invalid."
                + " Ensure that you are only accessing grain functionality from within the context of a grain.");
        }
    }
}