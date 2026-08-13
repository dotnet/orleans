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
        /// <param name="dueTime">The amount of time to delay before initially invoking the reminder. A value of <see cref="TimeSpan.Zero"/> means the first tick is scheduled immediately; negative values and <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> are rejected.</param>
        /// <param name="period">The time interval between invocations of the reminder. The value must be greater than <see cref="TimeSpan.Zero"/>; values of zero, negative, and <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> are rejected. The effective minimum is also constrained by <see cref="Orleans.Hosting.ReminderOptions.MinimumReminderPeriod"/>.</param>
        /// <returns>The reminder.</returns>
        /// <remarks>
        /// There is no special one-shot reminder value for <paramref name="period"/>. To schedule a single callback, register a valid positive period and unregister the reminder after the first callback fires.
        /// </remarks>
        Task<IGrainReminder> RegisterOrUpdateReminder(GrainId callingGrainId, string reminderName, TimeSpan dueTime, TimeSpan period);

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
        Task<IGrainReminder?> GetReminder(GrainId callingGrainId, string reminderName);

        /// <summary>
        /// Gets all reminders which are currently registered to the active grain.
        /// </summary>
        /// <param name="callingGrainId">The ID of the the currently executing grain</param>
        /// <returns>All reminders which are currently registered to the active grain.</returns>
        Task<List<IGrainReminder>> GetReminders(GrainId callingGrainId);
    }
}
