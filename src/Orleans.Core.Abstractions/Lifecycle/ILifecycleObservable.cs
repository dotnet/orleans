using System;

namespace Orleans
{
    /// <summary>
    /// Observable lifecycle.
    /// Each stage of the lifecycle is observable. All observers will be notified when the stage is reached when starting, and stopping.
    /// Stages are started in ascending order, and stopped in descending order.
    /// </summary>
    public interface ILifecycleObservable
    {
        /// <summary>
        /// Subscribe for notification when a stage is reached while starting or stopping.
        /// </summary>
        /// <param name="observerName">The name of the observer, for reporting purposes.</param>
        /// <param name="stage">The stage of to subscribe to.</param>
        /// <param name="observer">The observer.</param>
        /// <returns>A disposable that can be disposed of to unsubscribe.</returns>
        IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer);
    }
}
