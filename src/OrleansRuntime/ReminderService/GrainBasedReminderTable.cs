using System;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.MultiCluster;
using Orleans.Runtime.Configuration;


namespace Orleans.Runtime.ReminderService
{
    [Reentrant]
    [OneInstancePerCluster]
    internal class GrainBasedReminderTable : Grain, IReminderTableGrain
    {
        private InMemoryRemindersTable remTable;
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = LogManager.GetLogger(String.Format("GrainBasedReminderTable_{0}", Data.Address.ToString()), LoggerType.Runtime);
            logger.Info("GrainBasedReminderTable {0} Activated. Full identity: {1}", Identity, Data.Address.ToFullString());
            remTable = new InMemoryRemindersTable();
            base.DelayDeactivation(TimeSpan.FromDays(10 * 365)); // Delay Deactivation for GrainBasedReminderTable virtually indefinitely.
            return TaskDone.Done;
        }

        public Task Init(GlobalConfiguration config, Logger logger)
        {
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("GrainBasedReminderTable {0} OnDeactivateAsync. Full identity: {1}", Identity, Data.Address.ToFullString());
            return TaskDone.Done;
        }

        public Task<ReminderTableData> ReadRows(GrainReference grainRef)
        {
            return Task.FromResult(remTable.ReadRows(grainRef));
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            ReminderTableData t = remTable.ReadRows(begin, end);
            logger.Verbose("Read {0} reminders from memory: {1}, {2}", t.Reminders.Count, Environment.NewLine, Utils.EnumerableToString(t.Reminders));
            return Task.FromResult(t);
        }

        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            return Task.FromResult(remTable.ReadRow(grainRef, reminderName));
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            return Task.FromResult(remTable.UpsertRow(entry));
        }

        /// <summary>
        /// Remove a row from the table
        /// </summary>
        /// <param name="grainRef"></param>
        /// <param name="reminderName"></param>
        /// <param name="eTag"></param>
        /// <returns>true if a row with <paramref name="grainRef"/> and <paramref name="reminderName"/> existed and was removed successfully, false otherwise</returns>
        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            if (logger.IsVerbose) logger.Verbose("RemoveRow entry grainRef = {0}, reminderName = {1}, eTag = {2}", grainRef, reminderName, eTag);
            bool result = remTable.RemoveRow(grainRef, reminderName, eTag);
            if (result == false)
            {
                logger.Warn(ErrorCode.RS_Table_Remove, "RemoveRow failed for grainRef = {0}, ReminderName = {1}, eTag = {2}. Table now is: {3}",
                    grainRef.ToDetailedString(), reminderName, eTag, remTable.ReadAll());
            }
            return Task.FromResult(result);
        }

        public Task TestOnlyClearTable()
        {
            logger.Info("TestOnlyClearTable");
            remTable.Reset();
            return TaskDone.Done;
        }
    }
}
