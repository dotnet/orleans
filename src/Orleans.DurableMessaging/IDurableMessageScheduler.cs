using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableMessaging;

/// <summary>
/// Schedules a durable message for future delivery.
/// </summary>
public interface IDurableMessageScheduler
{
    /// <summary>
    /// Schedules <paramref name="message"/> for delivery at or after <paramref name="dueTime"/>.
    /// </summary>
    ValueTask ScheduleAsync(
        DurableEnvelope message,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken = default);
}
