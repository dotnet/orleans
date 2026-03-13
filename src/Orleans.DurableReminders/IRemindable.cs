using System.Threading.Tasks;

namespace Orleans.DurableReminders;

/// <summary>
/// Callback interface that grains must implement in order to register and receive durable reminders.
/// </summary>
public interface IRemindable : IGrain
{
    /// <summary>
    /// Receives a new durable reminder tick.
    /// </summary>
    Task ReceiveReminder(string reminderName, Runtime.TickStatus status);
}
