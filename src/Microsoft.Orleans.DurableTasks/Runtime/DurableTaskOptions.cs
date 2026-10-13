using System;

namespace Orleans.Configuration;

/// <summary>Configures durable grain RPC retention and cleanup.</summary>
public sealed class DurableTaskOptions
{
    /// <summary>Gets or sets how long terminal responses remain available for polling after completion.</summary>
    /// <remarks>
    /// Applies to tasks without a grain caller, such as tasks scheduled by clients, and to tasks whose completion message
    /// was dead-lettered. A task whose completion was delivered to its calling grain is pruned when the delivery retires.
    /// </remarks>
    public TimeSpan ResultRetentionPeriod { get; set; } = TimeSpan.FromDays(1);
}
