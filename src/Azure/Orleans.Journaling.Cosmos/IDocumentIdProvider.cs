namespace Orleans.Journaling.Cosmos;

public interface IDocumentIdProvider
{
    (string DocumentId, PartitionKey PartitionKey) GetIdentifiers(GrainId grainId);
}
