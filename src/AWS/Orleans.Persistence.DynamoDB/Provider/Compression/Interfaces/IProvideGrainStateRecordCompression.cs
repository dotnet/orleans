using Orleans.Storage;
using static Orleans.Persistence.DynamoDB.Provider.Compression.Interfaces.IGrainStateCompressionManager;

namespace Orleans.Persistence.DynamoDB.Provider.Compression.Interfaces
{
    public interface IProvideGrainStateRecordCompression
    {
        public BinaryStateCompression CompressionType { get; }

        public void Compress(DynamoDBGrainStorage.GrainStateRecord record);

        public void Decompress(DynamoDBGrainStorage.GrainStateRecord record);
    }
}
