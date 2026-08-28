using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans
{
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
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task StartAsync(CancellationToken cancellationToken = default)
#pragma warning disable CS0618 // Type or member is obsolete
            => Init();
#pragma warning restore CS0618 // Type or member is obsolete

        /// <summary>
        /// Initializes this instance.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Obsolete("Implement and use StartAsync instead")]
        Task Init() => Task.CompletedTask;

        /// <summary>
        /// Reads the reminder table entries associated with the specified grain.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <returns>The reminder table entries associated with the specified grain.</returns>
        Task<ReminderTableData> ReadRows(GrainId grainId);

        /// <summary>
        /// Reads the reminder table entries associated with the specified grain.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The reminder table entries associated with the specified grain.</returns>
        Task<ReminderTableData> ReadRows(GrainId grainId, CancellationToken cancellationToken) => ReadRows(grainId);

        /// <summary>
        /// Returns all rows that have their <see cref="GrainId.GetUniformHashCode"/> in the range (begin, end].
        /// If begin is greater or equal to end, returns all entries with hash greater begin or hash less or equal to end.
        /// </summary>
        /// <param name="begin">The exclusive lower bound.</param>
        /// <param name="end">The inclusive upper bound.</param>
        /// <returns>The reminder table entries which fall within the specified range.</returns>
        Task<ReminderTableData> ReadRows(uint begin, uint end);

        /// <summary>
        /// Returns all rows whose grain hash is in the specified range.
        /// </summary>
        /// <param name="begin">The exclusive lower bound.</param>
        /// <param name="end">The inclusive upper bound.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The reminder table entries which fall within the specified range.</returns>
        Task<ReminderTableData> ReadRows(uint begin, uint end, CancellationToken cancellationToken) => ReadRows(begin, end);

        /// <summary>
        /// Returns all rows in the range (begin, end], optionally requesting a strongly consistent discovery read.
        /// </summary>
        /// <param name="begin">The exclusive lower bound.</param>
        /// <param name="end">The inclusive upper bound.</param>
        /// <param name="requireStrongConsistency">
        /// <see langword="true"/> when the caller is establishing ownership and cannot rely on an eventually consistent discovery index.
        /// </param>
        /// <returns>The reminder table entries which fall within the specified range.</returns>
        Task<ReminderTableData> ReadRows(uint begin, uint end, bool requireStrongConsistency)
            => ReadRows(begin, end);

        /// <summary>
        /// Returns all rows in the range (begin, end], optionally requesting a strongly consistent discovery read.
        /// </summary>
        /// <param name="begin">The exclusive lower bound.</param>
        /// <param name="end">The inclusive upper bound.</param>
        /// <param name="requireStrongConsistency">Whether discovery must not depend on an eventually consistent index.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The reminder table entries which fall within the specified range.</returns>
        Task<ReminderTableData> ReadRows(
            uint begin,
            uint end,
            bool requireStrongConsistency,
            CancellationToken cancellationToken)
            => ReadRows(begin, end, requireStrongConsistency);

        /// <summary>
        /// Reads all rows in the provided ownership ranges using one provider-selected consistency operation.
        /// </summary>
        /// <param name="ranges">The owned ranges, each represented as an exclusive lower and inclusive upper bound.</param>
        /// <param name="requireStrongConsistency">Whether discovery must not depend on an eventually consistent index.</param>
        /// <returns>The reminder entries in the provided ranges.</returns>
        Task<ReminderTableData> ReadRows(
            IReadOnlyList<(uint Begin, uint End)> ranges,
            bool requireStrongConsistency)
            => ReadRows(ranges, requireStrongConsistency, CancellationToken.None);

        /// <summary>
        /// Reads all rows in the provided ownership ranges using one provider-selected consistency operation.
        /// </summary>
        /// <param name="ranges">The owned ranges, each represented as an exclusive lower and inclusive upper bound.</param>
        /// <param name="requireStrongConsistency">Whether discovery must not depend on an eventually consistent index.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The reminder entries in the provided ranges.</returns>
        async Task<ReminderTableData> ReadRows(
            IReadOnlyList<(uint Begin, uint End)> ranges,
            bool requireStrongConsistency,
            CancellationToken cancellationToken)
        {
            var result = new List<ReminderEntry>();
            foreach (var range in ranges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = await ReadRows(range.Begin, range.End, requireStrongConsistency, cancellationToken);
                if (rows is null)
                {
                    // Providers compiled against older Orleans versions can return null.
                    return null!;
                }

                result.AddRange(rows.Reminders);
            }
            return new(result);
        }
        }

        /// <summary>
        /// Reads the specified entry.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="reminderName">Name of the reminder.</param>
        /// <returns>The reminder table entry.</returns>
        Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName);

        /// <summary>
        /// Reads the specified entry.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="reminderName">Name of the reminder.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The reminder table entry.</returns>
        Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName, CancellationToken cancellationToken) => ReadRow(grainId, reminderName);

        /// <summary>
        /// Upserts the specified entry.
        /// </summary>
        /// <param name="entry">The entry.</param>
        /// <returns>The row's new ETag.</returns>
        Task<string?> UpsertRow(ReminderEntry entry);

        /// <summary>
        /// Upserts the specified entry.
        /// </summary>
        /// <param name="entry">The entry.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The row's new ETag.</returns>
        Task<string?> UpsertRow(ReminderEntry entry, CancellationToken cancellationToken) => UpsertRow(entry);

        /// <summary>
        /// Removes a row from the table.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// /// <param name="eTag">The ETag.</param>
        /// <returns>true if a row with <paramref name="grainId"/> and <paramref name="reminderName"/> existed and was removed successfully, false otherwise</returns>
        Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag);

        /// <summary>
        /// Removes a row from the table.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// <param name="eTag">The ETag.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true"/> if the row was removed; otherwise, <see langword="false"/>.</returns>
        Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag, CancellationToken cancellationToken) => RemoveRow(grainId, reminderName, eTag);

        /// <summary>
        /// Clears the table.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task TestOnlyClearTable();

        /// <summary>
        /// Clears the table.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task TestOnlyClearTable(CancellationToken cancellationToken) => TestOnlyClearTable();

        /// <summary>
        /// Stops the reminder table.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Reminder table interface for grain based implementation.
    /// </summary>
    internal interface IReminderTableGrain : IGrainWithIntegerKey
    {
        [Alias("EEEF6FCA")]
        Task<ReminderTableData> ReadRows(GrainId grainId, CancellationToken cancellationToken = default);

        [Alias("13558B55")]
        Task<ReminderTableData> ReadRows(uint begin, uint end, CancellationToken cancellationToken = default);

        [Alias("ECA791DE")]
        Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName, CancellationToken cancellationToken = default);

        [Alias("873299B5")]
        Task<string?> UpsertRow(ReminderEntry entry, CancellationToken cancellationToken = default);

        [Alias("FF391E0B")]
        Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag, CancellationToken cancellationToken = default);

        [Alias("8EBE0523")]
        Task TestOnlyClearTable(CancellationToken cancellationToken = default);
    }

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
        public ReminderTableData(IEnumerable<ReminderEntry> list)
        {
            Reminders = new List<ReminderEntry>(list);
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
        /// Gets the reminders.
        /// </summary>
        /// <value>The reminders.</value>
        [Id(0)]
        public IList<ReminderEntry> Reminders { get; private set; }

        /// <summary>
        /// Returns a <see cref="string" /> that represents this instance.
        /// </summary>
        /// <returns>A <see cref="string" /> that represents this instance.</returns>
        public override string ToString() => $"[{Reminders.Count} reminders: {Utils.EnumerableToString(Reminders)}.";
    }

    /// <summary>
    /// Represents a reminder table entry.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class ReminderEntry
    {
        /// <summary>
        /// Gets or sets the grain ID of the grain that created the reminder. Forms the reminder
        /// primary key together with <see cref="ReminderName"/>.
        /// </summary>
        [Id(0)]
        public GrainId GrainId { get; set; }

        /// <summary>
        /// Gets or sets the name of the reminder. Forms the reminder primary key together with 
        /// <see cref="GrainId"/>.
        /// </summary>
        [Id(1)]
        public string ReminderName { get; set; } = null!;

        /// <summary>
        /// Gets or sets the time when the reminder was supposed to tick in the first time
        /// </summary>
        [Id(2)]
        public DateTime StartAt { get; set; }

        /// <summary>
        /// Gets or sets the time period for the reminder
        /// </summary>
        [Id(3)]
        public TimeSpan Period { get; set; }

        /// <summary>
        /// Gets or sets the ETag.
        /// </summary>
        /// <value>The ETag.</value>
        [Id(4)]
        public string? ETag { get; set; }

        /// <inheritdoc/>
        public override string ToString() => $"<GrainId={GrainId} ReminderName={ReminderName} Period={Period}>";

        /// <summary>
        /// Returns an <see cref="IGrainReminder"/> representing the data in this instance.
        /// </summary>
        /// <returns>The <see cref="IGrainReminder"/>.</returns>
        internal IGrainReminder ToIGrainReminder() => new ReminderData(GrainId, ReminderName, ETag!);
    }

    [Serializable, GenerateSerializer, Immutable]
    internal sealed class ReminderData : IGrainReminder
    {
        [Id(0)]
        public readonly GrainId GrainId;
        [Id(1)]
        public string ReminderName { get; }
        [Id(2)]
        public readonly string ETag;

        internal ReminderData(GrainId grainId, string reminderName, string eTag)
        {
            GrainId = grainId;
            ReminderName = reminderName;
            ETag = eTag;
        }

        public override string ToString() => $"<IOrleansReminder: GrainId={GrainId} ReminderName={ReminderName} ETag={ETag}>";
    }
}
