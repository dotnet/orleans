using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Persistence.DynamoDB.Provider.Compression.Interfaces;

namespace Orleans.Persistence.DynamoDB.Provider.Compression;

public class GrainStateRecordDeflateCompressionService : GrainStateRecordCompressionServiceBase
{
    public GrainStateRecordDeflateCompressionService(
        DynamoDBStorageOptions options,
        ILogger<GrainStateRecordDeflateCompressionService> logger)
        : base(options, IGrainStateCompressionManager.BinaryStateCompression.Deflate, logger)
    {
    }

    protected override Stream GetCompressionStream(
        Stream targetStream,
        bool leaveOpen) =>
        new DeflateStream(targetStream, this.StateCompressionPolicy.CompressionLevel, leaveOpen);

    protected override Stream GetDecompressionStream(
        Stream sourceStream,
        bool leaveOpen) => new DeflateStream(sourceStream, CompressionMode.Decompress, leaveOpen);
}