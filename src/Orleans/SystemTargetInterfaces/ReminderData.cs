using System;
using Orleans.Runtime;

namespace Orleans
{
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