using System.Collections.Generic;
using Orleans.AdoNet.Core;

namespace Orleans.Reminders.AdoNet;

internal class RemindersStoredQueries(Dictionary<string, string> queries) : DbStoredQueries(queries)
{
    /// <summary>
    /// A query template to read reminder entries.
    /// </summary>
    internal string ReadReminderRowsKey => GetQuery(nameof(ReadReminderRowsKey));

    /// <summary>
    /// A query template to read reminder entries with ranges.
    /// </summary>
    internal string ReadRangeRows1Key => GetQuery(nameof(ReadRangeRows1Key));

    /// <summary>
    /// A query template to read reminder entries with ranges.
    /// </summary>
    internal string ReadRangeRows2Key => GetQuery(nameof(ReadRangeRows2Key));

    /// <summary>
    /// A query template to read a reminder entry with ranges.
    /// </summary>
    internal string ReadReminderRowKey => GetQuery(nameof(ReadReminderRowKey));

    /// <summary>
    /// A query template to upsert a reminder row.
    /// </summary>
    internal string UpsertReminderRowKey => GetQuery(nameof(UpsertReminderRowKey));

    /// <summary>
    /// A query template to delete a reminder row.
    /// </summary>
    internal string DeleteReminderRowKey => GetQuery(nameof(DeleteReminderRowKey));

    /// <summary>
    /// A query template to delete all reminder rows.
    /// </summary>
    internal string DeleteReminderRowsKey => GetQuery(nameof(DeleteReminderRowsKey));
}
