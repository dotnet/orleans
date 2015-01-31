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
using System.Collections.Generic;
using System.Linq;


namespace Orleans.Runtime.ReminderService
{
    [Serializable]
    internal class InMemoryRemindersTable
    {
        // key: GrainReference
        // value: V
        //      V.key: ReminderName
        //      V.Value: ReminderEntry
        private Dictionary<GrainReference, Dictionary<string, ReminderEntry>> reminderTable;

        // in our first version, we do not support 'updates', so we aren't using these
        // enable after adding updates ... even then, you will probably only need etags per row, not the whole
        // table version, as each read/insert/update should touch & depend on only one row at a time
        //internal TableVersion TableVersion;
        
        [NonSerialized] 
        private readonly TraceLogger logger = TraceLogger.GetLogger("InMemoryReminderTable", TraceLogger.LoggerType.Runtime);

        public InMemoryRemindersTable()
        {
            Reset();
        }

        public ReminderTableData ReadRows(GrainReference grainRef)
        {
            Dictionary<string, ReminderEntry> reminders;
            reminderTable.TryGetValue(grainRef, out reminders);
            return reminders == null ? new ReminderTableData() : 
                new ReminderTableData(reminders.Values.ToList());
        }
        
        /// <summary>
        /// Return all rows that have their GrainReference's.GetUniformHashCode() in the range (start, end]
        /// </summary>
        /// <param name="begin"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public ReminderTableData ReadRows(uint begin, uint end)
        {
            var range = RangeFactory.CreateRange(begin, end);
            IEnumerable<GrainReference> keys = reminderTable.Keys.Where(range.InRange);
      
            // is there a sleaker way of doing this in C#?
            var list = new List<ReminderEntry>();
            foreach (GrainReference k in keys)
                list.AddRange(reminderTable[k].Values);
            
            if (logger.IsVerbose3) logger.Verbose3("Selected {0} out of {1} reminders from memory for {2}. List is: {3}{4}", list.Count, reminderTable.Count, range.ToString(), 
                Environment.NewLine, Utils.EnumerableToString(list, e => e.ToString()));

            return new ReminderTableData(list);
        }
        
        /// <summary>
        /// Return all rows that have their GrainReference's.GetUniformHashCode() in the range (start, end]
        /// </summary>
        /// <param name="grainRef"></param>
        /// <param name="reminderName"></param>
        /// <returns></returns>
        public ReminderEntry ReadRow(GrainReference grainRef, string reminderName)
        {
            ReminderEntry r = reminderTable[grainRef][reminderName];
            if (logger.IsVerbose3) logger.Verbose3("Read for grain {0} reminder {1} row {2}", grainRef, reminderName, r.ToString());
            return r;
        }

        public string UpsertRow(ReminderEntry entry)
        {
            entry.ETag = Guid.NewGuid().ToString();
            Dictionary<string, ReminderEntry> d;
            if (!reminderTable.ContainsKey(entry.GrainRef))
            {
                d = new Dictionary<string, ReminderEntry>();
                reminderTable.Add(entry.GrainRef, d);
            }
            d = reminderTable[entry.GrainRef];

            ReminderEntry old; // tracing purposes only
            d.TryGetValue(entry.ReminderName, out old); // tracing purposes only
            // add or over-write
            d[entry.ReminderName] = entry;
            if (logger.IsVerbose3) logger.Verbose3("Upserted entry {0}, replaced {1}", entry, old);
            return entry.ETag;
        }
        
        /// <summary>
        /// Remove a row from the table
        /// </summary>
        /// <param name="grainRef"></param>
        /// <param name="reminderName"></param>
        /// /// <param name="eTag"></param>
        /// <returns>true if a row with <paramref name="grainRef"/> and <paramref name="reminderName"/> existed and was removed successfully, false otherwise</returns>
        public bool RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            Dictionary<string, ReminderEntry> data = null;
            ReminderEntry e = null;

            // assuming the calling grain executes one call at a time, so no need to lock 
            if (!reminderTable.TryGetValue(grainRef, out data))
            {
                logger.Info("1");
                return false;
            }

            data.TryGetValue(reminderName, out e); // check if eTag matches
            if (e == null || !e.ETag.Equals(eTag))
            {
                logger.Info("2");
                return false;
            }

            if (!data.Remove(reminderName))
            {
                logger.Info("3");
                return false;
            }

            if (data.Count == 0)
            {
                reminderTable.Remove(grainRef);
            }
            logger.Info("4");
            return true;
        }

        // use only for internal printing during testing ... the reminder table can be huge in a real deployment!
        public ReminderTableData ReadAll()
        {
            // is there a sleaker way of doing this in C#?
            var list = new List<ReminderEntry>();
            foreach (GrainReference k in reminderTable.Keys)
            {
                list.AddRange(reminderTable[k].Values);
            }            
            return new ReminderTableData(list);
        }

        public void Reset()
        {
            reminderTable = new Dictionary<GrainReference, Dictionary<string, ReminderEntry>>();
        }
    }
}
