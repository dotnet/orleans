namespace Orleans.DurableJobs;

/// <summary>
/// Specifies the order in which durable jobs with the same due time are dequeued.
/// </summary>
public enum DurableJobPriority : sbyte
{
    /// <summary>
    /// Dequeue after normal- and high-priority jobs with the same due time.
    /// </summary>
    Low = -1,

    /// <summary>
    /// Use the default dequeue order.
    /// </summary>
    Normal = 0,

    /// <summary>
    /// Dequeue before low- and normal-priority jobs with the same due time.
    /// </summary>
    High = 1,
}
