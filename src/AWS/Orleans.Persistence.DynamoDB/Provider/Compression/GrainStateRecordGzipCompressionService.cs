using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Persistence.DynamoDB.Provider.Compression.Interfaces;

namespace Orleans.Persistence.DynamoDB.Provider.Compression;

public class GrainStateRecordGzipCompressionService : GrainStateRecordCompressionServiceBase
{
    public GrainStateRecordGzipCompressionService(
        DynamoDBStorageOptions options,
        ILogger<GrainStateRecordGzipCompressionService> logger)
        : base(options, IGrainStateCompressionManager.BinaryStateCompression.GZip, logger)
    {
    }

    protected override Stream GetCompressionStream(
        Stream targetStream,
        bool leaveOpen) =>
        new GZipStream(targetStream, this.StateCompressionPolicy.CompressionLevel, leaveOpen);

    protected override Stream GetDecompressionStream(
        Stream sourceStream,
        bool leaveOpen) => new GZipStream(sourceStream, CompressionMode.Decompress, leaveOpen);
}