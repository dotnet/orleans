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

﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Concurrency;


namespace Orleans
{
    /// <summary>
    /// Interface for multiple implementations of the underlying storage for reminder data:
    /// Azure Table, SQL, development emulator grain, and a mock implementation.
    /// Defined as a grain interface for the development emulator grain case.
    /// </summary>
    [Unordered]
    internal interface IReminderTable : IGrain
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

    internal class ReminderTableData
    {
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
    internal class ReminderEntry
    {
        // 1 & 2 combine to form a unique key, i.e., a reminder is uniquely identified using these two together
        public GrainReference GrainRef { get; set; }        // 1
        public string ReminderName { get; set; }    // 2

        public DateTime StartAt { get; set; }
        public TimeSpan Period { get; set; }

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
    internal class ReminderData : IGrainReminder
    {
        public GrainReference GrainRef { get; private set; }
        public string ReminderName { get; private set; }
        public string ETag { get; private set; }

        internal ReminderData(GrainReference grainRef, string reminderName, string eTag)
        {
            GrainRef = grainRef;
            ReminderName = reminderName;
            ETag = eTag;
        }

        public override string ToString()
        {
            return string.Format("<IOrleansReminder: GrainRef={0} ReminderName={1} ETag={2}>", GrainRef.ToDetailedString(), ReminderName, ETag);
        }
    }
}
