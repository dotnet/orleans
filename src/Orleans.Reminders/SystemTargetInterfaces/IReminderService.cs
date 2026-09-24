using System;
using System.Collections.Generic;
using System.Threading;
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
        [Alias("Start")]
        Task Start();

        /// <summary>
        /// Starts the service.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("5CF78F8A")]
        Task Start(CancellationToken cancellationToken) => Start();

        /// <summary>
        /// Stops the service.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("Stop")]
        Task Stop();

        /// <summary>
        /// Stops the service.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("DCFCA00D")]
        Task Stop(CancellationToken cancellationToken) => Stop();

        /// <summary>
        /// Registers a new reminder or updates an existing one.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueTime">The amount of time to delay before firing the reminder initially.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <returns>The reminder.</returns>
        [Alias("RegisterOrUpdateReminder")]
        Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period);

        /// <summary>
        /// Registers a new reminder or updates an existing one.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is being registered or updated on behalf of.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="dueTime">The amount of time to delay before firing the reminder initially.</param>
        /// <param name="period">The time interval between invocations of the reminder.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The reminder.</returns>
        [Alias("1281C86D")]
        Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period, CancellationToken cancellationToken)
            => RegisterOrUpdateReminder(grainId, reminderName, dueTime, period);

        /// <summary>
        /// Unregisters the specified reminder.
        /// </summary>
        /// <param name="reminder">The reminder.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("UnregisterReminder")]
        Task UnregisterReminder(IGrainReminder reminder);

        /// <summary>
        /// Unregisters the specified reminder.
        /// </summary>
        /// <param name="reminder">The reminder.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("A7AF84A8")]
        Task UnregisterReminder(IGrainReminder reminder, CancellationToken cancellationToken) => UnregisterReminder(reminder);

        /// <summary>
        /// Reconciles a completed reminder mutation on the current owner, following a bounded number of topology redirects.
        /// </summary>
        /// <param name="grainId">The grain identity.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="remainingHops">The remaining topology redirects.</param>
        /// <returns><see langword="true"/> when an owner reconciled the mutation; otherwise <see langword="false"/>.</returns>
        Task<bool> ReconcileReminder(GrainId grainId, string reminderName, int remainingHops);

        /// <summary>
        /// Gets the reminder registered to the specified grain with the provided name.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is registered on.</param>
        /// <param name="reminderName">The name of the reminder.</param>
        /// <returns>The reminder.</returns>
        [Alias("GetReminder")]
        Task<IGrainReminder?> GetReminder(GrainId grainId, string reminderName);

        /// <summary>
        /// Gets the reminder registered to the specified grain with the provided name.
        /// </summary>
        /// <param name="grainId">A reference to the grain which the reminder is registered on.</param>
        /// <param name="reminderName">The name of the reminder.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The reminder.</returns>
        [Alias("AC622EEB")]
        Task<IGrainReminder?> GetReminder(GrainId grainId, string reminderName, CancellationToken cancellationToken)
            => GetReminder(grainId, reminderName);

        /// <summary>
        /// Gets all reminders registered for the specified grain.
        /// </summary>
        /// <param name="grainId">A reference to the grain.</param>
        /// <returns>A list of all registered reminders for the specified grain.</returns>
        [Alias("GetReminders")]
        Task<List<IGrainReminder>> GetReminders(GrainId grainId);

        /// <summary>
        /// Gets all reminders registered for the specified grain.
        /// </summary>
        /// <param name="grainId">A reference to the grain.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A list of all registered reminders for the specified grain.</returns>
        [Alias("419EB51E")]
        Task<List<IGrainReminder>> GetReminders(GrainId grainId, CancellationToken cancellationToken) => GetReminders(grainId);
    }
}
