using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans
{
    public class ReminderTableData
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
}