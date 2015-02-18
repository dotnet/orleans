using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace UnitTestGrains
{

    public interface IReminderTestGrain : IGrain
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
    public interface IReminderTestCopyGrain : IGrain
    {
        Task<IGrainReminder> StartReminder(string reminderName, TimeSpan? period = null, bool validate = false);
        Task StopReminder(string reminderName);

        Task<TimeSpan> GetReminderPeriod(string reminderName);
        Task<long> GetCounter(string name);
    }

    public interface IReminderGrainWrong : IGrain
    // since it doesnt implement IRemindable, we should get an error at run time
    // we need a way to let the user know at compile time if s/he doesn't implement IRemindable yet tries to register a reminder
    {
        Task<bool> StartReminder(string reminderName);
    }

    #region reproduce error with multiple interfaces

    //public interface IBaseGrain : IGrain //IRemindable
    //{
    //    Task SomeTestMethod();
    //}

    //public interface IDerivedGrain : IBaseGrain
    //{
    //}

    #endregion
}
