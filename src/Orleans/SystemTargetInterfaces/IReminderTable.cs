using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;


namespace Orleans
{
    /// <summary>
    /// Interface for multiple implementations of the underlying storage for reminder data:
    /// Azure Table, SQL, development emulator grain, and a mock implementation.
    /// Defined as a grain interface for the development emulator grain case.
    /// </summary>  
    public interface IReminderTable
    {
        Task Init(GlobalConfiguration config, Logger logger);

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
}
