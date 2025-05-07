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

        /// <summary>
        /// Use to write grain reference to the table entity
        /// </summary>
        string GetGrainReference(GrainReference grainReference);
        /// <summary>
        /// Use to read the reminder entity from table to in-memory grain reference
        /// </summary>
        GrainReference GetGrainReference(string grainRef);
    }
}
