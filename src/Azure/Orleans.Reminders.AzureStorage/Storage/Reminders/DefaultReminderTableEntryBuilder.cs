using Orleans.Runtime;
using Orleans.Runtime.ReminderService;

namespace Orleans.Reminders.AzureStorage.Storage.Reminders
{
    internal class DefaultReminderTableEntryBuilder : IReminderTableEntryBuilder
    {
        public static IReminderTableEntryBuilder Instance = new DefaultReminderTableEntryBuilder();

        public string ConstructPartitionKey(string serviceId, GrainReference grainReference)
            => ReminderTableEntry.ConstructPartitionKey(serviceId, grainReference);

        public string ConstructRowKey(GrainReference grainReference, string reminderName)
            => ReminderTableEntry.ConstructRowKey(grainReference, reminderName);

        public string GetGrainReference(GrainReference grainReference)
            => grainReference.ToKeyString();
    }
}
