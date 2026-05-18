using System.Threading.Tasks;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Callback interface that grains must implement in order to register and receive advanced reminders.
/// </summary>
public interface IRemindable : IGrain
{
    /// <summary>
    /// Receives a new advanced reminder tick.
    /// </summary>
    Task ReceiveReminder(string reminderName, Runtime.TickStatus status);
}
