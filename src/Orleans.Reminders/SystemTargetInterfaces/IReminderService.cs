using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Services;

namespace Orleans
{
    /// <summary>
    /// Functionality for managing reminders.
    /// </summary>
    public interface IReminderService : IGrainService
    {
        /// <summary>
        /// Starts the service.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Start();

        /// <summary>
        /// Stops the service.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Stop();

        /// <summary>
        /// Registers a new reminder or updates an existing one.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueTime">The amount of time to delay before firing the reminder initially.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period);

        /// <summary>
        /// Registers a new reminder or updates an existing one with an absolute UTC due timestamp.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueAtUtc">The UTC timestamp when the reminder should fire initially.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, DateTime dueAtUtc, TimeSpan period);

        /// <summary>
        /// Registers a new reminder or updates an existing one with adaptive delivery options.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueTime">The amount of time to delay before firing the reminder initially.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <param name="priority">The reminder priority.</param>
        /// <param name="action">The missed reminder action.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId grainId,
            string reminderName,
            TimeSpan dueTime,
            TimeSpan period,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action);

        /// <summary>
        /// Registers a new reminder or updates an existing one with an absolute UTC due timestamp and adaptive delivery options.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueAtUtc">The UTC timestamp when the reminder should fire initially.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <param name="priority">The reminder priority.</param>
        /// <param name="action">The missed reminder action.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId grainId,
            string reminderName,
            DateTime dueAtUtc,
            TimeSpan period,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action);

        /// <summary>
        /// Registers a new cron-based reminder or updates an existing one.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="cronExpression">The cron expression.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, string cronExpression);

        /// <summary>
        /// Registers a new cron-based reminder or updates an existing one with an explicit scheduling time zone.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="cronExpression">The cron expression.</param>
        /// <param name="cronTimeZoneId">The optional cron time zone id. Null or empty means UTC.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId grainId,
            string reminderName,
            string cronExpression,
            string cronTimeZoneId);

        /// <summary>
        /// Registers a new cron-based reminder or updates an existing one with adaptive delivery options.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="cronExpression">The cron expression.</param>
        /// <param name="priority">The reminder priority.</param>
        /// <param name="action">The missed reminder action.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId grainId,
            string reminderName,
            string cronExpression,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action);

        /// <summary>
        /// Registers a new cron-based reminder or updates an existing one with adaptive delivery options and an explicit scheduling time zone.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="cronExpression">The cron expression.</param>
        /// <param name="priority">The reminder priority.</param>
        /// <param name="action">The missed reminder action.</param>
        /// <param name="cronTimeZoneId">The optional cron time zone id. Null or empty means UTC.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId grainId,
            string reminderName,
            string cronExpression,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action,
            string cronTimeZoneId);

        /// <summary>
        /// Unregisters the specified reminder.
        /// </summary>
        /// <param name="reminder">The reminder.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task UnregisterReminder(IGrainReminder reminder);

        /// <summary>
        /// Gets the reminder registered to the specified grain with the provided name.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is registered on.</param>
        /// <param name="reminderName">The name of the reminder.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> GetReminder(GrainId grainId, string reminderName);

        /// <summary>
        /// Gets all reminders registered for the specified grain.
        /// </summary>
        /// <param name="grainId">A reference to the grain.</param>
        /// <returns>A list of all registered reminders for the specified grain.</returns>
        Task<List<IGrainReminder>> GetReminders(GrainId grainId);
    }
}
