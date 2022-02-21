using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface IReminderV2TestGrain2 : IGrainWithGuidKey
    {
        Task<IGrainReminderV2> StartReminder(string reminderName, TimeSpan? period = null, bool validate = false);

        Task StopReminder(string reminderName);
        Task StopReminder(IGrainReminderV2 reminder);

        Task<TimeSpan> GetReminderPeriod(string reminderName);
        Task<long> GetCounter(string name);
        Task<IGrainReminderV2> GetReminderObject(string reminderName);
        Task<List<IGrainReminderV2>> GetRemindersList();

        Task EraseReminderTable();
    }

    // to test reminders for different grain types
    public interface IReminderV2TestCopyGrain : IGrainWithGuidKey
    {
        Task<IGrainReminderV2> StartReminder(string reminderName, TimeSpan? period = null, bool validate = false);
        Task StopReminder(string reminderName);

        Task<TimeSpan> GetReminderPeriod(string reminderName);
        Task<long> GetCounter(string name);
    }

    public interface IReminderV2GrainWrong : IGrainWithIntegerKey
    // since it doesnt implement IRemindable, we should get an error at run time
    // we need a way to let the user know at compile time if s/he doesn't implement IRemindable yet tries to register a reminder
    {
        Task<bool> StartReminder(string reminderName);
    }
}

