using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Orleans.AdoNet.Core;
using Orleans.Runtime;

namespace Orleans.Reminders.AdoNet;

internal class RemindersRelationalOrleansQueries : RelationalOrleansQueries<RemindersStoredQueries>
{
    private RemindersRelationalOrleansQueries(IRelationalStorage storage, RemindersStoredQueries queries) : base(storage, queries)
    {
    }

    /// <summary>
    /// Creates an instance of a database of type <see cref="RemindersRelationalOrleansQueries"/> and Initializes Orleans queries from the database.
    /// Orleans uses only these queries and the variables therein, nothing more.
    /// </summary>
    /// <param name="invariantName">The invariant name of the connector for this database.</param>
    /// <param name="connectionString">The connection string this database should use for database operations.</param>
    internal new static async Task<RemindersRelationalOrleansQueries> CreateInstance(string invariantName, string connectionString)
    {
        var storage = RelationalStorage.CreateInstance(invariantName, connectionString);

        var queries = await storage.ReadAsync(DbStoredQueries.GetQueriesKey, DbStoredQueries.Converters.GetQueryKeyAndValue, null);

        return new RemindersRelationalOrleansQueries(storage, new RemindersStoredQueries(queries.ToDictionary(q => q.Key, q => q.Value)));
    }

    /// <summary>
    /// Reads Orleans reminder data from the tables.
    /// </summary>
    /// <param name="serviceId">The service ID.</param>
    /// <param name="grainId">The grain reference (ID).</param>
    /// <returns>Reminder table data.</returns>
    internal Task<ReminderTableData> ReadReminderRowsAsync(string serviceId, GrainId grainId)
    {
        return ReadAsync(Queries.ReadReminderRowsKey, GetReminderEntry, command =>
            new DbStoredQueries.Columns(command) { ServiceId = serviceId, GrainId = grainId.ToString() },
            ret => new ReminderTableData(ret.ToList()));
    }

    /// <summary>
    /// Reads Orleans reminder data from the tables.
    /// </summary>
    /// <param name="serviceId">The service ID.</param>
    /// <param name="beginHash">The begin hash.</param>
    /// <param name="endHash">The end hash.</param>
    /// <returns>Reminder table data.</returns>
    internal Task<ReminderTableData> ReadReminderRowsAsync(string serviceId, uint beginHash, uint endHash)
    {
        var query = (int)beginHash < (int)endHash ? Queries.ReadRangeRows1Key : Queries.ReadRangeRows2Key;

        return ReadAsync(query, GetReminderEntry, command =>
            new DbStoredQueries.Columns(command) { ServiceId = serviceId, BeginHash = beginHash, EndHash = endHash },
            ret => new ReminderTableData(ret.ToList()));
    }

    internal static KeyValuePair<string, string> GetQueryKeyAndValue(IDataRecord record)
    {
        return new KeyValuePair<string, string>(record.GetValue<string>("QueryKey"),
            record.GetValue<string>("QueryText"));
    }

    internal static ReminderEntry GetReminderEntry(IDataRecord record)
    {
        //Having non-null field, GrainId, means with the query filter options, an entry was found.
        var grainId = record.GetValueOrDefault<string>(nameof(DbStoredQueries.Columns.GrainId));
        return grainId == null
            ? null
            : new ReminderEntry
            {
                GrainId = GrainId.Parse(grainId),
                ReminderName = record.GetValue<string>(nameof(DbStoredQueries.Columns.ReminderName)),
                StartAt = record.GetDateTimeValue(nameof(DbStoredQueries.Columns.StartTime)),

                //Use the GetInt64 method instead of the generic GetValue<TValue> version to retrieve the value from the data record
                //GetValue<int> causes an InvalidCastException with oracle data provider. See https://github.com/dotnet/orleans/issues/3561
                Period = TimeSpan.FromMilliseconds(record.GetInt64(nameof(DbStoredQueries.Columns.Period))),
                ETag = DbStoredQueries.Converters.GetVersion(record).ToString()
            };
    }

    /// <summary>
    /// Reads one row of reminder data.
    /// </summary>
    /// <param name="serviceId">Service ID.</param>
    /// <param name="grainId">The grain reference (ID).</param>
    /// <param name="reminderName">The reminder name to retrieve.</param>
    /// <returns>A remainder entry.</returns>
    internal Task<ReminderEntry> ReadReminderRowAsync(string serviceId, GrainId grainId,
        string reminderName)
    {
        return ReadAsync(Queries.ReadReminderRowKey, GetReminderEntry, command =>
            new DbStoredQueries.Columns(command)
            {
                ServiceId = serviceId,
                GrainId = grainId.ToString(),
                ReminderName = reminderName
            }, ret => ret.FirstOrDefault());
    }

    /// <summary>
    /// Either inserts or updates a reminder row.
    /// </summary>
    /// <param name="serviceId">The service ID.</param>
    /// <param name="grainId">The grain reference (ID).</param>
    /// <param name="reminderName">The reminder name to retrieve.</param>
    /// <param name="startTime">Start time of the reminder.</param>
    /// <param name="period">Period of the reminder.</param>
    /// <returns>The new etag of the either or updated or inserted reminder row.</returns>
    internal Task<string> UpsertReminderRowAsync(string serviceId, GrainId grainId,
        string reminderName, DateTime startTime, TimeSpan period)
    {
        return ReadAsync(Queries.UpsertReminderRowKey, DbStoredQueries.Converters.GetVersion, command =>
            new DbStoredQueries.Columns(command)
            {
                ServiceId = serviceId,
                GrainHash = grainId.GetUniformHashCode(),
                GrainId = grainId.ToString(),
                ReminderName = reminderName,
                StartTime = startTime,
                Period = period
            }, ret => ret.First().ToString());
    }

    /// <summary>
    /// Deletes a reminder
    /// </summary>
    /// <param name="serviceId">Service ID.</param>
    /// <param name="grainId"></param>
    /// <param name="reminderName"></param>
    /// <param name="etag"></param>
    /// <returns></returns>
    internal Task<bool> DeleteReminderRowAsync(string serviceId, GrainId grainId, string reminderName,
        string etag)
    {
        return ReadAsync(Queries.DeleteReminderRowKey, DbStoredQueries.Converters.GetSingleBooleanValue, command =>
            new DbStoredQueries.Columns(command)
            {
                ServiceId = serviceId,
                GrainId = grainId.ToString(),
                ReminderName = reminderName,
                Version = etag
            }, ret => ret.First());
    }

    /// <summary>
    /// Deletes all reminders rows of a service id.
    /// </summary>
    /// <param name="serviceId"></param>
    /// <returns></returns>
    internal Task DeleteReminderRowsAsync(string serviceId)
    {
        return ExecuteAsync(Queries.DeleteReminderRowsKey, command =>
            new DbStoredQueries.Columns(command) { ServiceId = serviceId });
    }
}
