using System;
using Orleans.Runtime;

namespace Orleans
{
    [Serializable]
    public class ReminderEntry
    {
        /// <summary>
        /// The grain reference of the grain that created the reminder. Forms the reminder
        /// primary key together with <see cref="ReminderName"/>.
        /// </summary>
        public GrainReference GrainRef { get; set; }

        /// <summary>
        /// The name of the reminder. Forms the reminder primary key together with 
        /// <see cref="GrainRef"/>.
        /// </summary>
        public string ReminderName { get; set; }

        /// <summary>
        /// the time when the reminder was supposed to tick in the first time
        /// </summary>
        public DateTime StartAt { get; set; }

        /// <summary>
        /// the time period for the reminder
        /// </summary>
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
}