#nullable enable
namespace Orleans.Runtime;

/// <summary>
/// Provides functionality to record the creation and deletion of grain timers.
/// </summary>
internal interface IGrainTimerRegistry
{
    /// <summary>
    /// Signals to the registry that a timer was created.
    /// </summary>
    /// <param name="timer">
    /// The timer.
    /// </param>
    void OnTimerCreated(IGrainTimer timer);

    /// <summary>
    /// Signals to the registry that a timer was disposed.
    /// </summary>
    /// <param name="timer">
    /// The timer.
    /// </param>
    void OnTimerDisposed(IGrainTimer timer);
}

