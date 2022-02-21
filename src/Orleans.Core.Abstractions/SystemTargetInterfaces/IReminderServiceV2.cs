using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Services;

namespace Orleans
{
    /// <summary>
    /// Functionality for managing reminders V2.
    /// </summary>
    public interface IReminderServiceV2 : IGrainService
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
        /// <param name="grainRef">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueTime">The amount of time to delay before firing the reminder initially.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminderV2> RegisterOrUpdateReminder(GrainReference grainRef, string reminderName, TimeSpan dueTime, TimeSpan period);

        /// <summary>
        /// Unregisters the specified reminder.
        /// </summary>
        /// <param name="reminder">The reminder.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task UnregisterReminder(IGrainReminderV2 reminder);

        /// <summary>
        /// Gets the reminder registered to the specified grain with the provided name.
        /// </summary>
        /// <param name="grainRef">A reference to the grain which the reminder is registered on.</param>
        /// <param name="reminderName">The name of the reminder.</param>
        /// <returns>The reminder.</returns>
        Task<IGrainReminderV2> GetReminder(GrainReference grainRef, string reminderName);

        /// <summary>
        /// Gets all reminders registered for the specified grain.
        /// </summary>
        /// <param name="grainRef">A reference to the grain.</param>
        /// <returns>A list of all registered reminders for the specified grain.</returns>
        Task<List<IGrainReminderV2>> GetReminders(GrainReference grainRef);
    }
}
