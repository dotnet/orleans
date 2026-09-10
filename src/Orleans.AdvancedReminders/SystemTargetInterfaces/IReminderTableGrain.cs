namespace Orleans.AdvancedReminders;

/// <summary>
/// Reminder table interface for grain based implementation.
/// </summary>
internal interface IReminderTableGrain : IGrainWithIntegerKey
{
    Task<ReminderTableData> ReadRows(GrainId grainId);

    Task<ReminderTableData> ReadRows(uint begin, uint end);

    Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken);

    Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName);

    Task<string> UpsertRow(ReminderEntry entry);

    Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag);

    Task TestOnlyClearTable();
}
