namespace Orleans.AdvancedReminders;

/// <summary>
/// Represents a collection of reminder table entries.
/// </summary>
[Serializable]
[GenerateSerializer]
public sealed class ReminderTableData
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderTableData"/> class.
    /// </summary>
    /// <param name="list">The entries.</param>
    public ReminderTableData(IEnumerable<ReminderEntry> list, string? continuationToken = null)
    {
        Reminders = new List<ReminderEntry>(list);
        ContinuationToken = continuationToken;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderTableData"/> class.
    /// </summary>
    /// <param name="entry">The entry.</param>
    public ReminderTableData(ReminderEntry entry)
    {
        Reminders = new[] { entry };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderTableData"/> class.
    /// </summary>
    public ReminderTableData()
    {
        Reminders = Array.Empty<ReminderEntry>();
    }

    /// <summary>
    /// Gets the opaque continuation token for the next page, or <see langword="null"/> when the range is exhausted.
    /// </summary>
    [Id(0)]
    public string? ContinuationToken { get; private set; }

    /// <summary>
    /// Gets the reminders.
    /// </summary>
    /// <value>The reminders.</value>
    [Id(1)]
    public IList<ReminderEntry> Reminders { get; private set; }

    /// <summary>
    /// Returns a <see cref="string" /> that represents this instance.
    /// </summary>
    /// <returns>A <see cref="string" /> that represents this instance.</returns>
    public override string ToString() => $"[{Reminders.Count} reminders: {Utils.EnumerableToString(Reminders)}].";
}
