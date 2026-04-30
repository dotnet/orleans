using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

#nullable disable
namespace Orleans.TestingHost.UnixSocketTransport;

public partial class UnixSocketConnectionOptions
{
    /// <summary>
    /// Get or sets to function used to get a filename given an endpoint
    /// </summary>
    public Func<EndPoint, string> ConvertEndpointToPath { get; set; } = DefaultConvertEndpointToPath;

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex ConvertEndpointRegex();

    private static string DefaultConvertEndpointToPath(EndPoint endPoint) => Path.Combine(Path.GetTempPath(), ConvertEndpointRegex().Replace(endPoint.ToString(), "_"));
}
