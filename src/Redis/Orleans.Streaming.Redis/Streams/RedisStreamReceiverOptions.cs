using System;

namespace Orleans.Configuration;

/// <summary>
/// Redis streaming receiver options.
/// </summary>
public sealed class RedisStreamReceiverOptions
{
    /// <summary>
    /// Redis streams message field name.
    /// </summary>
    public string FieldName { get; set; } = DefaultFieldName;
    public const string DefaultFieldName = "payload";

    /// <summary>
    /// The number of entries to fetch from Redis in a single read operation.
    /// </summary>
    public int ReadCount { get; set; } = DefaultReadCount;

    /// <summary>
    /// The default number of entries to fetch from Redis in a single read operation.
    /// </summary>
    public const int DefaultReadCount = 1000;
}
