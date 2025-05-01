using Orleans.Runtime;
using Orleans.Runtime.ReminderService;

namespace Orleans.Reminders.AzureStorage.Storage.Reminders
{
    internal class DefaultReminderTableEntryBuilder : IReminderTableEntryBuilder
    {
        private readonly IGrainReferenceRuntime _grainReferenceRuntime;

        public DefaultReminderTableEntryBuilder(IGrainReferenceRuntime grainReferenceRuntime)
        {
            _grainReferenceRuntime = grainReferenceRuntime;
        }

        public string ConstructPartitionKey(string serviceId, GrainReference grainReference)
            => ReminderTableEntry.ConstructPartitionKey(serviceId, grainReference);

        public string ConstructRowKey(GrainReference grainReference, string reminderName)
            => ReminderTableEntry.ConstructRowKey(grainReference, reminderName);

        public string GetGrainReference(GrainReference grainReference)
            => grainReference.ToKeyString();

        public GrainReference GetGrainReference(string grainRef)
            => GrainReference.FromKeyString(grainRef, _grainReferenceRuntime);
    }
}
