using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Reminders.Cron.Internal;
using Orleans.Runtime.Services;
using Orleans.Timers;

#nullable enable
namespace Orleans.Runtime.ReminderService
{
    internal sealed class ReminderRegistry : GrainServiceClient<IReminderService>, IReminderRegistry
    {
        private IServiceProvider? serviceProvider;
        private readonly ReminderOptions options;

        public ReminderRegistry(IServiceProvider serviceProvider, IOptions<ReminderOptions> options) : base(serviceProvider)
        {
            this.serviceProvider = serviceProvider;
            this.options = options.Value;
        }

        public Task<IGrainReminder> RegisterOrUpdateReminder(GrainId callingGrainId, string reminderName, TimeSpan dueTime, TimeSpan period)
            => RegisterOrUpdateReminder(
                callingGrainId,
                reminderName,
                dueTime,
                period,
                Runtime.ReminderPriority.Normal,
                Runtime.MissedReminderAction.Skip);

        public Task<IGrainReminder> RegisterOrUpdateReminder(GrainId callingGrainId, string reminderName, DateTime dueAtUtc, TimeSpan period)
            => RegisterOrUpdateReminder(
                callingGrainId,
                reminderName,
                dueAtUtc,
                period,
                Runtime.ReminderPriority.Normal,
                Runtime.MissedReminderAction.Skip);

        public Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId callingGrainId,
            string reminderName,
            TimeSpan dueTime,
            TimeSpan period,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action)
        {
            // Perform input volatility checks 
            if (dueTime == Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(dueTime), "Cannot use InfiniteTimeSpan dueTime to create a reminder");

            if (dueTime.Ticks < 0)
                throw new ArgumentOutOfRangeException(nameof(dueTime), "Cannot use negative dueTime to create a reminder");

            if (period == Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(period), "Cannot use InfiniteTimeSpan period to create a reminder");

            if (period.Ticks < 0)
                throw new ArgumentOutOfRangeException(nameof(period), "Cannot use negative period to create a reminder");
            
            var minReminderPeriod = options.MinimumReminderPeriod;
            if (period < minReminderPeriod)
                throw new ArgumentException($"Cannot register reminder {reminderName} as requested period ({period}) is less than minimum allowed reminder period ({minReminderPeriod})");

            if (string.IsNullOrEmpty(reminderName))
                throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));

            ValidatePriorityAndAction(priority, action);

            EnsureReminderServiceRegisteredAndInGrainContext();
            return GetGrainService(callingGrainId).RegisterOrUpdateReminder(callingGrainId, reminderName, dueTime, period, priority, action);
        }

        public Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId callingGrainId,
            string reminderName,
            DateTime dueAtUtc,
            TimeSpan period,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action)
        {
            if (dueAtUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Due timestamp must use DateTimeKind.Utc.", nameof(dueAtUtc));

            if (period == Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(period), "Cannot use InfiniteTimeSpan period to create a reminder");

            if (period.Ticks < 0)
                throw new ArgumentOutOfRangeException(nameof(period), "Cannot use negative period to create a reminder");

            var minReminderPeriod = options.MinimumReminderPeriod;
            if (period < minReminderPeriod)
                throw new ArgumentException($"Cannot register reminder {reminderName} as requested period ({period}) is less than minimum allowed reminder period ({minReminderPeriod})");

            if (string.IsNullOrEmpty(reminderName))
                throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));

            ValidatePriorityAndAction(priority, action);

            EnsureReminderServiceRegisteredAndInGrainContext();
            return GetGrainService(callingGrainId).RegisterOrUpdateReminder(callingGrainId, reminderName, dueAtUtc, period, priority, action);
        }

        public Task<IGrainReminder> RegisterOrUpdateReminder(GrainId callingGrainId, string reminderName, string cronExpression)
            => RegisterOrUpdateReminder(
                callingGrainId,
                reminderName,
                cronExpression,
                priority: Runtime.ReminderPriority.Normal,
                action: Runtime.MissedReminderAction.Skip,
                cronTimeZoneId: null);

        public Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId callingGrainId,
            string reminderName,
            string cronExpression,
            string? cronTimeZoneId)
            => RegisterOrUpdateReminder(
                callingGrainId,
                reminderName,
                cronExpression,
                priority: Runtime.ReminderPriority.Normal,
                action: Runtime.MissedReminderAction.Skip,
                cronTimeZoneId: cronTimeZoneId);

        public Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId callingGrainId,
            string reminderName,
            string cronExpression,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action)
            => RegisterOrUpdateReminder(
                callingGrainId,
                reminderName,
                cronExpression,
                priority: priority,
                action: action,
                cronTimeZoneId: null);

        public Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId callingGrainId,
            string reminderName,
            string cronExpression,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action,
            string? cronTimeZoneId)
        {
            if (string.IsNullOrWhiteSpace(reminderName))
                throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));

            if (string.IsNullOrWhiteSpace(cronExpression))
                throw new ArgumentException("Cannot use null or empty cron expression for the reminder", nameof(cronExpression));

            var schedule = ReminderCronSchedule.Parse(cronExpression, cronTimeZoneId);

            ValidatePriorityAndAction(priority, action);

            EnsureReminderServiceRegisteredAndInGrainContext();
            return GetGrainService(callingGrainId).RegisterOrUpdateReminder(
                callingGrainId,
                reminderName,
                schedule.Expression.ToExpressionString(),
                priority,
                action,
                schedule.TimeZoneId);
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
                    + "\n  * Microsoft.Orleans.Reminders via ISiloBuilder.AddAdaptiveReminderService(...)"
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

        private static void ValidatePriorityAndAction(Runtime.ReminderPriority priority, Runtime.MissedReminderAction action)
        {
            if (!Enum.IsDefined(priority))
            {
                throw new ArgumentOutOfRangeException(nameof(priority), priority, "Invalid reminder priority.");
            }

            if (!Enum.IsDefined(action))
            {
                throw new ArgumentOutOfRangeException(nameof(action), action, "Invalid missed reminder action.");
            }
        }

    }
}
