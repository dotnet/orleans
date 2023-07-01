namespace UnitTests.GrainInterfaces
{
    public interface IReminderTestGrain : IGrainWithIntegerKey
    {
        Task<bool> IsReminderExists(string reminderName);
        Task AddReminder(string reminderName);
        Task RemoveReminder(string reminderName);
    }
}