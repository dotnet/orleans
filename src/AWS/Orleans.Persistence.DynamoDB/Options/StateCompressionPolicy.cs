using System.IO.Compression;
using Orleans.Persistence.DynamoDB.Provider.Compression.Interfaces;

namespace Orleans.Configuration;

public class StateCompressionPolicy
{
    /// <summary>
    /// Compression needs to be enabled explicitly
    /// </summary>
    public bool IsEnabled { get; set; } = false;

    /// <summary>
    /// Only compress the state if the uncompressed state size in bytes is above the threshold
    /// </summary>
    public int CompressStateIfAboveByteCount { get; set; } = 0;

    public IGrainStateCompressionManager.BinaryStateCompression Compression { get; set; } = IGrainStateCompressionManager.BinaryStateCompression.GZip;

    /// <summary>
    /// Use the fastest compression by default
    /// </summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;
}