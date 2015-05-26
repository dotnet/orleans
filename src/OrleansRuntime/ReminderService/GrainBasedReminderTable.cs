/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Threading.Tasks;

using Orleans.Concurrency;


namespace Orleans.Runtime.ReminderService
{
    [Reentrant]
    internal class GrainBasedReminderTable : Grain, IReminderTable
    {
        private InMemoryRemindersTable remTable;
        private TraceLogger logger;

        public override Task OnActivateAsync()
        {
            logger = TraceLogger.GetLogger(String.Format("GrainBasedReminderTable_{0}", Data.Address.ToString()), TraceLogger.LoggerType.Runtime);
            logger.Info("GrainBasedReminderTable {0} Activated. Full identity: {1}", Identity, Data.Address.ToFullString());
            remTable = new InMemoryRemindersTable();
            base.DelayDeactivation(TimeSpan.FromDays(10 * 365)); // Delay Deactivation for GrainBasedReminderTable virtually indefinitely.
            return TaskDone.Done;
        }

        public Task Init()
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
