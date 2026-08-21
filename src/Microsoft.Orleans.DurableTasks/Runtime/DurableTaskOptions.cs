using System;

namespace Orleans.Configuration;

/// <summary>Configures durable grain RPC retention and cleanup.</summary>
public sealed class DurableTaskOptions
{
    /// <summary>Gets or sets how long terminal responses remain available for polling after all callers acknowledge them.</summary>
    public TimeSpan ResultRetentionPeriod { get; set; } = TimeSpan.FromDays(1);
}
