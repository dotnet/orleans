using System;

namespace Orleans.Configuration;

/// <summary>Configures durable grain RPC retention and cleanup.</summary>
public sealed class DurableTaskOptions
{
    /// <summary>Gets or sets how long terminal responses remain available for polling after completion.</summary>
    /// <remarks>
    /// Applies to tasks without a grain caller, such as tasks scheduled by clients, and to tasks whose completion message
    /// was dead-lettered, measured from the dead letter. When the period ends, the task is deleted with its execution
    /// tree: polls report <see cref="Orleans.DurableTasks.DurableTaskNotFoundException"/>, and scheduling the same task
    /// identifier executes it again, so a task identifier deduplicates schedules for this period after completion. A
    /// task whose completion was delivered to its calling grain is deleted when the delivery retires.
    /// </remarks>
    public TimeSpan ResultRetentionPeriod { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets how often a grain which holds a stalled execution wakes, so that a later activation replays the
    /// execution.
    /// </summary>
    /// <remarks>
    /// An execution stalls when the running code can't replay it: it recorded a version which the code doesn't support,
    /// or the code diverged from its record. The task stays pending, and the grain keeps a wake-up at this interval, so
    /// once its activation ends, such as when a deployment stops its silo, the grain reactivates and replays the execution
    /// with the code which the new activation runs. The interval is positive; the default is one minute.
    /// </remarks>
    public TimeSpan StalledExecutionRetryInterval { get; set; } = TimeSpan.FromMinutes(1);
}
