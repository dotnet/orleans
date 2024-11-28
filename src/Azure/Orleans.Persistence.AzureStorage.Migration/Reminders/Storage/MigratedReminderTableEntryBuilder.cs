using Orleans.Persistence.Migration;
using Orleans.Reminders.AzureStorage.Storage.Reminders;
using Orleans.Runtime;

namespace Orleans.Persistence.AzureStorage.Migration.Reminders.Storage
{
    /// <summary>
    /// Builder for constructing reminder table entries for migrated data
    /// </summary>
    public class MigratedReminderTableEntryBuilder : IReminderTableEntryBuilder
    {
        IGrainReferenceExtractor grainReferenceExtractor;

        public MigratedReminderTableEntryBuilder(IGrainReferenceExtractor grainReferenceExtractor)
        {
            this.grainReferenceExtractor = grainReferenceExtractor;
        }

        public string ConstructPartitionKey(string serviceId, GrainReference grainReference)
            => $"{serviceId}_{grainReference.GrainIdentity.GetUniformHashCode():X}";

        public string ConstructRowKey(GrainReference grainReference, string reminderName)
        {
            var grainId = grainReferenceExtractor.GetGrainId(grainReference);
            return AzureTableUtils.SanitizeTableProperty($"{grainId}-{reminderName}");
        }

        public string GetGrainReference(GrainReference grainReference)
            => grainReferenceExtractor.GetGrainId(grainReference);
    }
}
