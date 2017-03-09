namespace Orleans.Runtime
{
    /// <summary>
    /// Handle for a persistent Reminder.
    /// </summary>
    public interface IGrainReminder
    {
        /// <summary> Name of this Reminder. </summary>
        string ReminderName { get; }
    }
}