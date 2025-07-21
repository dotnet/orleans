using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streaming.AzureStorage.Providers.Streams.AzureQueue.Json;

/// <summary>
/// Configuration options for the Azure Queue JSON data adapter.
/// </summary>
[Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
public class AzureQueueJsonDataAdapterOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to enable fallback to binary serialization when JSON serialization fails.
    /// Default is true.
    /// </summary>
    public bool EnableFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to prefer JSON serialization/deserialization first, then fallback to binary.
    /// When false, it will try binary serialization/deserialization first, then fallback to JSON.
    /// </summary>
    public bool PreferJson { get; set; } = false;
}
