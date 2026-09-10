namespace Orleans.AdvancedReminders.Runtime;

/// <summary>
/// Exception related to Orleans advanced reminder functions or reminder service.
/// </summary>
[GenerateSerializer]
public sealed class ReminderException : OrleansException
{
    public ReminderException(string message) : base(message)
    {
    }
}
