using System.Threading;

namespace Orleans.DurableTasks.Runtime;

/// <summary>A wait of an attempt for an event which a message delivers, which resumes once.</summary>
/// <remarks>
/// The runtime resumes the wait synchronously when it completes the task or signal record which the wait awaits, and the
/// wait's token resumes it when canceled, so the attempt counts as runnable before its waiting code continues.
/// </remarks>
internal sealed class ParkedWait(GrainDurableTaskContext attempt)
{
    private int _resumed;

    /// <summary>Resumes the wait, once.</summary>
    public void Resume()
    {
        if (Interlocked.Exchange(ref _resumed, 1) == 0)
        {
            attempt.OnWaitResumed();
        }
    }

    /// <summary>Ends the wait, which resumed or didn't.</summary>
    public void End() => attempt.OnWaitEnded(resumed: Interlocked.Exchange(ref _resumed, 1) != 0);
}
