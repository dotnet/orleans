using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// Callback interface that grains must implement inorder to be able to register and receive Reminders.
    /// </summary>
    public interface IRemindable : IGrain
    {
        /// <summary>
        /// Receive a new Reminder.
        /// </summary>
        /// <param name="reminderName">Name of this Reminder</param>
        /// <param name="status">Status of this Reminder tick</param>
        /// <returns>Completion promise which the grain will resolve when it has finished processing this Reminder tick.</returns>
        Task ReceiveReminder(string reminderName, Runtime.TickStatus status);
    }

    namespace Runtime
    {

        #region App visible exceptions

        #endregion
    }
}
