using System;

namespace Orleans.Configuration;

/// <summary>
/// Redis streaming receiver options.
/// </summary>
public sealed class RedisStreamReceiverOptions
{
    /// <summary>
    /// Redis streams consumer group name.
    /// </summary>
    public string ConsumerGroupName { get; set; } = DefaultConsumerGroupName;
    public const string DefaultConsumerGroupName = "orleans";

    /// <summary>
    /// Redis streams consumer name.
    /// </summary>
    public string ConsumerName { get; set; } = DefaultConsumerName;
    public const string DefaultConsumerName = "pullingagent";

    /// <summary>
    /// Redis streams message field name.
    /// </summary>
    public string FieldName { get; set; } = DefaultFieldName;
    public const string DefaultFieldName = "payload";

    /// <summary>
    /// Delivered streams message timeout.
    /// </summary>
    public TimeSpan DeliveredMessageIdleTimeout { get; set; } = DefaultDeliveredMessageIdleTimeout;
    public static readonly TimeSpan DefaultDeliveredMessageIdleTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MinDeliveredMessageIdleTimeout = TimeSpan.FromMilliseconds(200);
}
