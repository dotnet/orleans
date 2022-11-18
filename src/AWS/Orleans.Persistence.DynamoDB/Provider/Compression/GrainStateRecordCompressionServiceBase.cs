using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Persistence.DynamoDB.Provider.Compression.Interfaces;
using Orleans.Storage;

namespace Orleans.Persistence.DynamoDB.Provider.Compression;

public abstract class GrainStateRecordCompressionServiceBase : IProvideGrainStateRecordCompression
{
    private readonly ILogger logger;

    protected readonly StateCompressionPolicy StateCompressionPolicy;

    protected GrainStateRecordCompressionServiceBase(
        DynamoDBStorageOptions options,
        IGrainStateCompressionManager.BinaryStateCompression compressionType,
        ILogger logger)
    {
        this.StateCompressionPolicy = options?.StateCompressionPolicy ?? new StateCompressionPolicy();
        this.CompressionType = compressionType;
        this.logger = logger;
    }

    public IGrainStateCompressionManager.BinaryStateCompression CompressionType { get; }

    public void Compress(DynamoDBGrainStorage.GrainStateRecord record)
    {
        using var inputStream = new MemoryStream(record.BinaryState, false);
        using var compressedStream = new MemoryStream();
        using var compressorStream = this.GetCompressionStream(compressedStream, true);

        inputStream.CopyTo(compressorStream);
        compressorStream.Flush();

        var binaryStateLengthBeforeCompression = record.BinaryState.Length;
        record.BinaryState = compressedStream.ToArray();
        var binaryStateLengthAfterCompression = record.BinaryState.Length;
        record.BinaryStateProperties.Add(GrainStateCompressionManager.CompressionPropertyName, this.CompressionType.ToString());
        this.logger.LogDebug("Compressed grain state with reference={0}, type={1}, etag={2} from size={3} to size={4}",
            record.GrainReference, record.GrainType, record.ETag,
            binaryStateLengthBeforeCompression, binaryStateLengthAfterCompression);
    }

    public void Decompress(DynamoDBGrainStorage.GrainStateRecord record)
    {
        if (!record.BinaryStateProperties.TryGetValue(GrainStateCompressionManager.CompressionPropertyName, out var binaryStateCompressionTypeString)
            || !Enum.TryParse<IGrainStateCompressionManager.BinaryStateCompression>(binaryStateCompressionTypeString, true, out var binaryStateCompressionRecordProperty)
            || binaryStateCompressionRecordProperty != this.CompressionType)
        {
            this.logger.LogWarning("State properties does not contain the compression property {0}={1}", GrainStateCompressionManager.CompressionPropertyName, this.CompressionType);
            return;
        }

        using var compressedStream = new MemoryStream(record.BinaryState);
        using var decompressorStream = this.GetDecompressionStream(compressedStream, true);
        using var decompressedStream = new MemoryStream();

        decompressorStream.CopyTo(decompressedStream);

        var binaryStateLengthBeforeDecompression = record.BinaryState.Length;
        record.BinaryState = decompressedStream.ToArray();
        var binaryStateLengthAfterDecompression = record.BinaryState.Length;
        record.BinaryStateProperties.Remove(GrainStateCompressionManager.CompressionPropertyName);

        this.logger.LogDebug("Decompressed grain state with reference={0}, type={1}, etag={2} from size={3} to size={4}",
            record.GrainReference, record.GrainType.Length, record.ETag,
            binaryStateLengthBeforeDecompression, binaryStateLengthAfterDecompression);
    }
    
    protected abstract Stream GetCompressionStream(Stream targetStream, bool leaveOpen);

    protected abstract Stream GetDecompressionStream(Stream sourceStream, bool leaveOpen);
}