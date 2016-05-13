using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface IReminderTestGrain2 : IGrainWithGuidKey
    {
        Task<IGrainReminder> StartReminder(string reminderName, TimeSpan? period = null, bool validate = false);

        Task StopReminder(string reminderName);
        Task StopReminder(IGrainReminder reminder);

        Task<TimeSpan> GetReminderPeriod(string reminderName);
        Task<long> GetCounter(string name);
        Task<IGrainReminder> GetReminderObject(string reminderName);
        Task<List<IGrainReminder>> GetRemindersList();

        Task EraseReminderTable();
    }

    // to test reminders for different grain types
    public interface IReminderTestCopyGrain : IGrainWithGuidKey
    {
        Task<IGrainReminder> StartReminder(string reminderName, TimeSpan? period = null, bool validate = false);
        Task StopReminder(string reminderName);

        Task<TimeSpan> GetReminderPeriod(string reminderName);
        Task<long> GetCounter(string name);
    }

    public interface IReminderGrainWrong : IGrainWithIntegerKey
    // since it doesnt implement IRemindable, we should get an error at run time
    // we need a way to let the user know at compile time if s/he doesn't implement IRemindable yet tries to register a reminder
    {
        Task<bool> StartReminder(string reminderName);
    }
}

