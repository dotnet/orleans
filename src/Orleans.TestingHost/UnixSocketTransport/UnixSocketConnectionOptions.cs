using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace Orleans.TestingHost.UnixSocketTransport;

/// <summary>
/// Options for the Orleans Unix domain socket transport.
/// </summary>
public partial class UnixSocketConnectionOptions
{
    /// <summary>
    /// Gets or sets the function which maps an endpoint to a Unix domain socket path.
    /// </summary>
    public Func<EndPoint, string> ConvertEndpointToPath { get; set; } = DefaultConvertEndpointToPath;

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex ConvertEndpointRegex();

    private static string DefaultConvertEndpointToPath(EndPoint endPoint) => Path.Combine(Path.GetTempPath(), ConvertEndpointRegex().Replace(endPoint.ToString()!, "_"));
}
