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
        IGrainReferenceExtractor _grainReferenceExtractor;

        public MigratedReminderTableEntryBuilder(IGrainReferenceExtractor grainReferenceExtractor)
        {
            this._grainReferenceExtractor = grainReferenceExtractor;
        }

        public string ConstructPartitionKey(string serviceId, GrainReference grainReference)
            => $"{serviceId}_{grainReference.GrainIdentity.GetUniformHashCode():X}";

        public string ConstructRowKey(GrainReference grainReference, string reminderName)
        {
            var grainId = _grainReferenceExtractor.GetGrainId(grainReference);
            return AzureTableUtils.SanitizeTableProperty($"{grainId}-{reminderName}");
        }

        public string GetGrainReference(GrainReference grainReference)
            => _grainReferenceExtractor.GetGrainId(grainReference);

        public GrainReference GetGrainReference(string grainId)
            => _grainReferenceExtractor.ResolveGrainReference(grainId);
    }
}
