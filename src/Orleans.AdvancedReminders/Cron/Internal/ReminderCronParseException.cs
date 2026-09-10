namespace Orleans.AdvancedReminders.Cron.Internal;

internal sealed class ReminderCronParseException : FormatException
{
    public ReminderCronParseException(string message) : base(message) { }

    public ReminderCronParseException(string message, Exception innerException) : base(message, innerException) { }
}
