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

    [GenerateSerializer]
    public record class ReminderTickRecord(
        [property: Id(0)] string ReminderName,
        [property: Id(1)] DateTime CurrentTickTime,
        [property: Id(2)] DateTime FirstTickTime,
        [property: Id(3)] TimeSpan Period,
        [property: Id(4)] ReminderScheduleKind ScheduleKind);

    public interface IReminderTestGrain2 : IGrainWithGuidKey
    {
        Task<IGrainReminder> StartReminder(string reminderName, TimeSpan? period = null, bool validate = false);
        Task<IGrainReminder> StartReminderWithOptions(
            string reminderName,
            TimeSpan dueTime,
            TimeSpan period,
            ReminderPriority priority = ReminderPriority.Normal,
            MissedReminderAction action = MissedReminderAction.Skip,
            bool validate = false);
        Task<IGrainReminder> StartReminderAtUtc(
            string reminderName,
            DateTime dueAtUtc,
            TimeSpan period,
            ReminderPriority priority = ReminderPriority.Normal,
            MissedReminderAction action = MissedReminderAction.Skip,
            bool validate = false);
        Task<IGrainReminder> StartCronReminder(
            string reminderName,
            string cronExpression,
            ReminderPriority priority = ReminderPriority.Normal,
            MissedReminderAction action = MissedReminderAction.Skip,
            bool validate = false);
        Task UpsertRawReminderEntry(
            string reminderName,
            DateTime startAtUtc,
            TimeSpan period,
            string cronExpression,
            DateTime? nextDueUtc,
            ReminderPriority priority,
            MissedReminderAction action);

        Task StopReminder(string reminderName);
        Task StopReminder(IGrainReminder reminder);

        Task<TimeSpan> GetReminderPeriod(string reminderName);
        Task<(TimeSpan DueTime, TimeSpan Period)> GetReminderDueTimeAndPeriod(string reminderName);
        Task<long> GetCounter(string name);
        Task<IGrainReminder> GetReminderObject(string reminderName);
        Task<List<IGrainReminder>> GetRemindersList();

        Task EraseReminderTable();

        Task<Dictionary<string, ReminderState>> GetReminderStates();
        Task<List<ReminderTickRecord>> GetTickRecords();
        Task<List<ReminderTickRecord>> GetTickRecords(string reminderName);
        Task<ReminderEntry> GetReminderEntry(string reminderName);
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
