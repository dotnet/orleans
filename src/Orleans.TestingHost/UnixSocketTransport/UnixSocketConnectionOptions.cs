using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using Orleans.Networking.Shared;

namespace Orleans.TestingHost.UnixSocketTransport;

public partial class UnixSocketConnectionOptions
{
    /// <summary>
    /// Get or sets to function used to get a filename given an endpoint
    /// </summary>
    public Func<EndPoint, string> ConvertEndpointToPath { get; set; } = DefaultConvertEndpointToPath;

    /// <summary>
    /// Gets or sets the memory pool factory.
    /// </summary>
    internal Func<MemoryPool<byte>> MemoryPoolFactory { get; set; } = () => KestrelMemoryPool.Create();

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex ConvertEndpointRegex();

    private static string DefaultConvertEndpointToPath(EndPoint endPoint) => Path.Combine(Path.GetTempPath(), ConvertEndpointRegex().Replace(endPoint.ToString(), "_"));
}