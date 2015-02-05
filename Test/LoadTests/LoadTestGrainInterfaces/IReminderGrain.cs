using System;
using System.Threading.Tasks;

using Orleans;

namespace LoadTestGrainInterfaces
{
    public interface IReminderGrain : IGrain
    {
        /// <summary>
        /// Registers the named reminder.
        /// </summary>
        /// <param name="reminderName">Reminder to activate.</param>=
        /// <param name="period">Frequency period for this reminder</param>
        /// <param name="duration">How long to keep the reminder registered for. Any tick after duration will cause a deregister.</param>
        /// <param name="skipGet">Whether to skip the GetReminder call before calling RegisterOrUpdateReminder</param>
        /// <returns>True if the reminder was registered successfully; false otherwise.</returns>
        Task<bool> RegisterReminder(string reminderName, TimeSpan period, TimeSpan duration, bool skipGet);

        /// <summary>
        /// Unregisters the named reminder.
        /// </summary>
        /// <param name="reminderName">Name of the reminder</param>
        /// <returns>
        /// True if the reminder existed and was unregistered successfully or if the reminder did not
        /// exist; false otherwise.
        /// </returns>
        Task<bool> UnregisterReminder(string reminderName);
    }
}
