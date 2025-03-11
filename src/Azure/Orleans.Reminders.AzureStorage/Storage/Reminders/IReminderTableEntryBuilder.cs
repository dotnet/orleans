using Orleans.Runtime;

namespace Orleans.Reminders.AzureStorage.Storage.Reminders
{
    /// <summary>
    /// Constructor for Reminder table entity
    /// </summary>
    public interface IReminderTableEntryBuilder
    {
        string ConstructPartitionKey(string serviceId, GrainReference grainReference);
        string ConstructRowKey(GrainReference grainReference, string reminderName);

        string GetGrainReference(GrainReference grainReference);
    }
}
