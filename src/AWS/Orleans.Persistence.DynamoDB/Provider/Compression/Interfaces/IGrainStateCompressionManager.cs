using Orleans.Storage;

namespace Orleans.Persistence.DynamoDB.Provider.Compression.Interfaces;

public interface IGrainStateCompressionManager
{
    /// <summary>
    /// WARN: Do not rename the enum or the enum values. They are used to mark the binary state properties and they will be stored in DynamoDB along with the state binary data
    /// </summary>
    public enum BinaryStateCompression
    {
        GZip,
        Deflate
    }

    public void Compress(DynamoDBGrainStorage.GrainStateRecord record);

    public void Decompress(DynamoDBGrainStorage.GrainStateRecord record);
}