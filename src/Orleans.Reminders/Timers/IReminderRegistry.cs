using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Services;

namespace Orleans.Timers
{
    /// <summary>
    /// Functionality for managing reminders.
    /// </summary>
    public interface IReminderRegistry : IGrainServiceClient<IReminderService>
    {
        /// <summary>
        /// Register or update the reminder with the specified name for the currently active grain.
        /// </summary>
        /// <param name="callingGrainId">The ID of the the currently executing grain</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueTime">The amount of time to delay before initially invoking the reminder.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(GrainId callingGrainId, string reminderName, TimeSpan dueTime, TimeSpan period);

        /// <summary>
        /// Register or update the reminder with the specified name for the currently active grain using an absolute UTC due timestamp.
        /// </summary>
        /// <param name="callingGrainId">The ID of the currently executing grain.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueAtUtc">The UTC timestamp for the first tick.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(GrainId callingGrainId, string reminderName, DateTime dueAtUtc, TimeSpan period);

        /// <summary>
        /// Register or update the reminder with adaptive delivery options for the currently active grain.
        /// </summary>
        /// <param name="callingGrainId">The ID of the currently executing grain.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueTime">The amount of time to delay before initially invoking the reminder.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <param name="priority">The reminder priority.</param>
        /// <param name="action">The missed reminder action.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId callingGrainId,
            string reminderName,
            TimeSpan dueTime,
            TimeSpan period,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action);

        /// <summary>
        /// Register or update the reminder with adaptive delivery options for the currently active grain using an absolute UTC due timestamp.
        /// </summary>
        /// <param name="callingGrainId">The ID of the currently executing grain.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueAtUtc">The UTC timestamp for the first tick.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <param name="priority">The reminder priority.</param>
        /// <param name="action">The missed reminder action.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId callingGrainId,
            string reminderName,
            DateTime dueAtUtc,
            TimeSpan period,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action);

        /// <summary>
        /// Register or update the cron reminder with the specified name for the currently active grain.
        /// </summary>
        /// <param name="callingGrainId">The ID of the currently executing grain.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="cronExpression">The cron expression.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(GrainId callingGrainId, string reminderName, string cronExpression);

        /// <summary>
        /// Register or update the cron reminder with the specified name and time zone for the currently active grain.
        /// </summary>
        /// <param name="callingGrainId">The ID of the currently executing grain.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="cronExpression">The cron expression.</param>
        /// <param name="cronTimeZoneId">The optional cron time zone id. Null or empty means UTC.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId callingGrainId,
            string reminderName,
            string cronExpression,
            string cronTimeZoneId);

        /// <summary>
        /// Register or update the cron reminder with adaptive delivery options for the currently active grain.
        /// </summary>
        /// <param name="callingGrainId">The ID of the currently executing grain.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="cronExpression">The cron expression.</param>
        /// <param name="priority">The reminder priority.</param>
        /// <param name="action">The missed reminder action.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId callingGrainId,
            string reminderName,
            string cronExpression,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action);

        /// <summary>
        /// Register or update the cron reminder with adaptive delivery options for the currently active grain.
        /// </summary>
        /// <param name="callingGrainId">The ID of the currently executing grain.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="cronExpression">The cron expression.</param>
        /// <param name="priority">The reminder priority.</param>
        /// <param name="action">The missed reminder action.</param>
        /// <param name="cronTimeZoneId">The optional cron time zone id. Null or empty means UTC.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId callingGrainId,
            string reminderName,
            string cronExpression,
            Runtime.ReminderPriority priority,
            Runtime.MissedReminderAction action,
            string cronTimeZoneId);

        /// <summary>
        /// Unregisters a reminder from the currently active grain.
        /// </summary>
        /// <param name="callingGrainId">The ID of the the currently executing grain</param>
        /// <param name="reminder">The reminder to unregister.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task UnregisterReminder(GrainId callingGrainId, IGrainReminder reminder);

        /// <summary>
        /// Gets the reminder with the specified name which is registered to the currently active grain.
        /// </summary>
        /// <param name="callingGrainId">The ID of the the currently executing grain</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminder> GetReminder(GrainId callingGrainId, string reminderName);

        /// <summary>
        /// Gets all reminders which are currently registered to the active grain.
        /// </summary>
        /// <param name="callingGrainId">The ID of the the currently executing grain</param>
        /// <returns>All reminders which are currently registered to the active grain.</returns>
        Task<List<IGrainReminder>> GetReminders(GrainId callingGrainId);
    }
}
