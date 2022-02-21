using System;
using System.Collections.Generic;
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
    public interface IReminderV2Table
    {
        /// <summary>
        /// Initializes this instance.
        /// </summary>
        /// <returns>A Task representing the work performed.</returns>
        Task Init();

        /// <summary>
        /// Reads the reminder table entries associated with the specified grain.
        /// </summary>
        /// <param name="key">The grain.</param>
        /// <returns>The reminder table entries associated with the specified grain.</returns>
        Task<ReminderV2TableData> ReadRows(GrainReference key);

        /// <summary>
        /// Return all rows that have their GrainReference's.GetUniformHashCode() in the range (start, end]
        /// </summary>
        /// <param name="begin">The exclusive lower bound.</param>
        /// <param name="end">The inclusive upper bound.</param>
        /// <returns>The reminder table entries which fall within the specified range.</returns>
        Task<ReminderV2TableData> ReadRows(uint begin, uint end);

        /// <summary>
        /// Reads a specifie entry.
        /// </summary>
        /// <param name="grainRef">The grain reference.</param>
        /// <param name="reminderName">Name of the reminder.</param>
        /// <returns>The reminder table entry.</returns>
        Task<ReminderV2Entry> ReadRow(GrainReference grainRef, string reminderName);

        /// <summary>
        /// Upserts the specified entry.
        /// </summary>
        /// <param name="entry">The entry.</param>
        /// <returns>The row's new ETag.</returns>
        Task<string> UpsertRow(ReminderV2Entry entry);

        /// <summary>
        /// Removes a row from the table.
        /// </summary>
        /// <param name="grainRef">The grain reference.</param>
        /// <param name="reminderName">The reminder name.</param>
        /// /// <param name="eTag">The ETag.</param>
        /// <returns>true if a row with <paramref name="grainRef"/> and <paramref name="reminderName"/> existed and was removed successfully, false otherwise</returns>
        Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag);

        /// <summary>
        /// Clears the table.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task TestOnlyClearTable();
    }

    /// <summary>
    /// Reminder table interface for grain based implementation.
    /// </summary>
    [Unordered]
    internal interface IReminderV2TableGrain : IGrainWithIntegerKey
    {
        Task<ReminderV2TableData> ReadRows(GrainReference key);

        Task<ReminderV2TableData> ReadRows(uint begin, uint end);

        Task<ReminderV2Entry> ReadRow(GrainReference grainRef, string reminderName);

        Task<string> UpsertRow(ReminderV2Entry entry);

        Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag);

        Task TestOnlyClearTable();
    }

    /// <summary>
    /// Represents a collection of reminder table entries.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class ReminderV2TableData
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ReminderV2TableData"/> class.
        /// </summary>
        /// <param name="list">The entries.</param>
        public ReminderV2TableData(IEnumerable<ReminderV2Entry> list)
        {
            Reminders = new List<ReminderV2Entry>(list);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ReminderV2TableData"/> class.
        /// </summary>
        /// <param name="entry">The entry.</param>
        public ReminderV2TableData(ReminderV2Entry entry)
        {
            Reminders = new List<ReminderV2Entry> { entry };
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ReminderV2TableData"/> class.
        /// </summary>
        public ReminderV2TableData()
        {
            Reminders = new List<ReminderV2Entry>();
        }

        /// <summary>
        /// Gets the reminders.
        /// </summary>
        /// <value>The reminders.</value>
        [Id(0)]
        public IList<ReminderV2Entry> Reminders { get; private set; }
        /// <summary>
        /// Returns a <see cref="System.String" /> that represents this instance.
        /// </summary>
        /// <returns>A <see cref="System.String" /> that represents this instance.</returns>
        public override string ToString()
        {
            return string.Format("[{0} reminders: {1}.", Reminders.Count,
                Utils.EnumerableToString(Reminders, e => e.ToString()));
        }
    }

    /// <summary>
    /// Represents a reminder table entry.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class ReminderV2Entry
    {
        /// <summary>
        /// Gets or sets the grain reference of the grain that created the reminder. Forms the reminder
        /// primary key together with <see cref="ReminderName"/>.
        /// </summary>
        [Id(1)]
        public GrainReference GrainRef { get; set; }

        /// <summary>
        /// Gets or sets the name of the reminder. Forms the reminder primary key together with 
        /// <see cref="GrainRef"/>.
        /// </summary>
        [Id(2)]
        public string ReminderName { get; set; }

        /// <summary>
        /// Gets or sets the time when the reminder was supposed to tick in the first time
        /// </summary>
        [Id(3)]
        public DateTime StartAt { get; set; }

        /// <summary>
        /// Gets or sets the time period for the reminder
        /// </summary>
        [Id(4)]
        public TimeSpan Period { get; set; }

        /// <summary>
        /// Gets or sets the ETag.
        /// </summary>
        /// <value>The ETag.</value>
        [Id(5)]
        public string ETag { get; set; }

        /// <inheritdoc/>
        public override string ToString()
        {
            return string.Format("<GrainRef={0} ReminderName={1} Period={2}>", GrainRef.ToString(), ReminderName, Period);
        }

        /// <summary>
        /// Returns an <see cref="IGrainReminderV2"/> representing the data in this instance.
        /// </summary>
        /// <returns>The <see cref="IGrainReminderV2"/>.</returns>
        internal IGrainReminderV2 ToIGrainReminderV2()
        {
            return new ReminderV2Data(GrainRef, ReminderName, ETag);
        }
    }

    [Serializable]
    [GenerateSerializer]
    internal class ReminderV2Data : IGrainReminderV2
    {
        [Id(1)]
        public GrainReference GrainRef { get; private set; }
        [Id(2)]
        public string ReminderName { get; private set; }
        [Id(3)]
        public string ETag { get; private set; }

        internal ReminderV2Data(GrainReference grainRef, string reminderName, string eTag)
        {
            GrainRef = grainRef;
            ReminderName = reminderName;
            ETag = eTag;
        }

        public override string ToString()
        {
            return string.Format("<IOrleansReminder: GrainRef={0} ReminderName={1} ETag={2}>", GrainRef.ToString(), ReminderName, ETag);
        }
    }
}
