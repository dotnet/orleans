using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Interface for multiple implementations of the underlying storage for reminder data:
    /// Azure Table, SQL, development emulator grain, and a mock implementation.
    /// Defined as a grain interface for the development emulator grain case.
    /// </summary>  
    public interface IReminderTable
    {
        Task Init();

        Task<ReminderTableData> ReadRows(GrainReference key);

        /// <summary>
        /// Return all rows that have their GrainReference's.GetUniformHashCode() in the range (start, end]
        /// </summary>
        /// <param name="begin"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        Task<ReminderTableData> ReadRows(uint begin, uint end);

        Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName);

        Task<string> UpsertRow(ReminderEntry entry);

        /// <summary>
        /// Remove a row from the table.
        /// </summary>
        /// <param name="grainRef"></param>
        /// <param name="reminderName"></param>
        /// /// <param name="eTag"></param>
        /// <returns>true if a row with <paramref name="grainRef"/> and <paramref name="reminderName"/> existed and was removed successfully, false otherwise</returns>
        Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag);

        Task TestOnlyClearTable();
    }

    /// <summary>
    /// Reminder table interface for grain based implementation.
    /// </summary>
    [Unordered]
    internal interface IReminderTableGrain : IGrainWithIntegerKey
    {
        Task<ReminderTableData> ReadRows(GrainReference key);

        Task<ReminderTableData> ReadRows(uint begin, uint end);

        Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName);

        Task<string> UpsertRow(ReminderEntry entry);

        Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag);

        Task TestOnlyClearTable();
    }

    [Serializable]
    [GenerateSerializer]
    public class ReminderTableData
    {
        [Id(0)]
        public IList<ReminderEntry> Reminders { get; private set; }

        public ReminderTableData(IEnumerable<ReminderEntry> list)
        {
            Reminders = new List<ReminderEntry>(list);
        }

        public ReminderTableData(ReminderEntry entry)
        {
            Reminders = new List<ReminderEntry> {entry};
        }

        public ReminderTableData()
        {
            Reminders = new List<ReminderEntry>();
        }

        public override string ToString()
        {
            return string.Format("[{0} reminders: {1}.", Reminders.Count, 
                Utils.EnumerableToString(Reminders, e => e.ToString()));
        }
    }


    [Serializable]
    [GenerateSerializer]
    public class ReminderEntry
    {
        /// <summary>
        /// The grain reference of the grain that created the reminder. Forms the reminder
        /// primary key together with <see cref="ReminderName"/>.
        /// </summary>
        [Id(1)]
        public GrainReference GrainRef { get; set; }

        /// <summary>
        /// The name of the reminder. Forms the reminder primary key together with 
        /// <see cref="GrainRef"/>.
        /// </summary>
        [Id(2)]
        public string ReminderName { get; set; }

        /// <summary>
        /// the time when the reminder was supposed to tick in the first time
        /// </summary>
        [Id(3)]
        public DateTime StartAt { get; set; }

        /// <summary>
        /// the time period for the reminder
        /// </summary>
        [Id(4)]
        public TimeSpan Period { get; set; }

        [Id(5)]
        public string ETag { get; set; }

        public override string ToString()
        {
            return string.Format("<GrainRef={0} ReminderName={1} Period={2}>", GrainRef.ToString(), ReminderName, Period);
        }

        internal IGrainReminder ToIGrainReminder()
        {
            return new ReminderData(GrainRef, ReminderName, ETag);
        }
    }

    [Serializable]
    [GenerateSerializer]
    internal class ReminderData : IGrainReminder
    {
        [Id(1)]
        public GrainReference GrainRef { get; private set; }
        [Id(2)]
        public string ReminderName { get; private set; }
        [Id(3)]
        public string ETag { get; private set; }

        internal ReminderData(GrainReference grainRef, string reminderName, string eTag)
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
