using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    [GenerateSerializer]
    public record class ReminderState([property: Id(0)] IGrainReminder Reminder)
    {
        [Id(1)] public DateTime? Registered { get; init; } = null;
        [Id(2)] public DateTime? Unregistered { get; init; } = null;
        [Id(3)] public List<DateTime> Fired { get; init; } = new();
        [Id(4)] public List<(DateTime, string)> Log { get; init; } = new();
    }

    public interface IReminderTestGrain2 : IGrainWithGuidKey
    {
        Task<IGrainReminder> StartReminder(string reminderName, TimeSpan? period = null, bool validate = false);

        Task StopReminder(string reminderName);
        Task StopReminder(IGrainReminder reminder);

        Task<TimeSpan> GetReminderPeriod(string reminderName);
        Task<(TimeSpan DueTime, TimeSpan Period)> GetReminderDueTimeAndPeriod(string reminderName);
        Task<long> GetCounter(string name);
        Task<IGrainReminder> GetReminderObject(string reminderName);
        Task<List<IGrainReminder>> GetRemindersList();

        Task EraseReminderTable();

        Task<Dictionary<string, ReminderState>> GetReminderStates();
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
    // since the grain doesnt implement IRemindable, we should get an error at run time
    // we need a way to let the user know at compile time if IRemindable isn't implemented and tries to register a reminder
    {
        Task<bool> StartReminder(string reminderName);
    }
}

