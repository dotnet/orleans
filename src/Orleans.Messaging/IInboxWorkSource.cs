using System;

namespace Orleans.Messaging;

/// <summary>
/// Reports durable work which a component holds outside the inbox, such as messages which an
/// <see cref="IInboxMessageSink"/> accepted, so the inbox's durable wake-up reactivates the grain while that work remains.
/// </summary>
/// <remarks>
/// <para>
/// The inbox keeps one durable wake-up job per grain while it, the outbox, or any registered work source holds work.
/// Before each journal write, the inbox consults the work sources and ensures the wake-up, so a write which makes work
/// pending also guarantees that the grain is reactivated after a deactivation or crash. Hold the work in journaled
/// state of the grain's journal, and resume it when the grain activates: a wake-up reactivates the grain, and the work
/// source's owner continues its work from the recovered state.
/// </para>
/// <para>
/// Each wake-up job run consults the work sources again. It reschedules after the shortest delay any source reports,
/// and the wake-up ends once no source, the inbox, nor the outbox holds work. Wake-ups may be early, late, or duplicated,
/// including while the grain is active and running the work.
/// </para>
/// </remarks>
public interface IInboxWorkSource
{
    /// <summary>Gets the delay before the grain must be woken for this source's pending work.</summary>
    /// <returns>
    /// <see langword="null"/> when the source holds no pending work; otherwise, the delay after which a wake-up should
    /// reactivate the grain if it isn't active. A delay of <see cref="TimeSpan.Zero"/> or less requests a prompt
    /// wake-up. For work which runs as soon as the grain is active, return the longest delay acceptable before a
    /// deactivated grain resumes it.
    /// </returns>
    /// <remarks>Runs synchronously on the activation scheduler, before each journal write and on each wake-up.</remarks>
    TimeSpan? GetWakeupDelay();
}
