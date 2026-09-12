namespace Orleans.AdvancedReminders;

/// <summary>
/// Interface for implementations of the underlying storage for reminder data:
/// Azure Table, SQL, development emulator grain, and a mock implementation.
/// Defined as a grain interface for the development emulator grain case.
/// </summary>
public interface IReminderTable
{
    /// <summary>
    /// Initializes this instance.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the work performed.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the reminder table entries associated with the specified grain.
    /// </summary>
    /// <param name="grainId">The grain ID.</param>
    /// <returns>The reminder table entries associated with the specified grain.</returns>
    Task<ReminderTableData> ReadRows(GrainId grainId);

    /// <summary>
    /// Returns all rows that have their <see cref="GrainId.GetUniformHashCode"/> in the range (begin, end].
    /// If begin is greater or equal to end, returns all entries with hash greater begin or hash less or equal to end.
    /// </summary>
    /// <param name="begin">The exclusive lower bound.</param>
    /// <param name="end">The inclusive upper bound.</param>
    /// <returns>The reminder table entries which fall within the specified range.</returns>
    Task<ReminderTableData> ReadRows(uint begin, uint end);

    /// <summary>
    /// Returns a bounded page of rows that have their <see cref="GrainId.GetUniformHashCode"/> in the range (begin, end].
    /// </summary>
    /// <param name="begin">The exclusive lower bound.</param>
    /// <param name="end">The inclusive upper bound.</param>
    /// <param name="maxRows">The maximum number of rows to return.</param>
    /// <param name="continuationToken">An opaque continuation token returned by the preceding call, or <see langword="null"/>.</param>
    /// <returns>A bounded page of reminder rows and a continuation token when more rows remain.</returns>
    Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken);

    /// <summary>
    /// Reads the specified entry.
    /// </summary>
    /// <param name="grainId">The grain ID.</param>
    /// <param name="reminderName">Name of the reminder.</param>
    /// <returns>The reminder table entry, or <see langword="null"/> when it does not exist.</returns>
    Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName);

    /// <summary>
    /// Upserts the specified entry.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The row's new ETag.</returns>
    Task<string> UpsertRow(ReminderEntry entry);

    /// <summary>
    /// Removes a row from the table.
    /// </summary>
    /// <param name="grainId">The grain ID.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="eTag">The ETag.</param>
    /// <returns>true if a row with <paramref name="grainId"/> and <paramref name="reminderName"/> existed and was removed successfully, false otherwise</returns>
    Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag);

    /// <summary>
    /// Clears the table.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the work performed.</returns>
    Task TestOnlyClearTable();

    /// <summary>
    /// Stops the reminder table.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the work performed.</returns>
    Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
