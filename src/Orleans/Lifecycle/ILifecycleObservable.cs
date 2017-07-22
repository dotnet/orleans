﻿using System;

namespace Orleans
{
    /// <summary>
    /// Observable lifecycle.
    /// Each stage of lifecycle is observable.  All observers will be notified 
    ///   when the stage is reached when starting, and stopping.
    /// Stages are started in ascending order, and stopped in decending order.
    /// </summary>
    /// <typeparam name="TStage"></typeparam>
    public interface ILifecycleObservable<in TStage>
    {
        /// <summary>
        /// Subscribe for notification when a stage is reached while starting or stopping.
        /// </summary>
        /// <param name="stage">stage of interest</param>
        /// <param name="observer">stage observer</param>
        /// <returns>A disposable that can be disposed of to unsubscribe</returns>
        IDisposable Subscribe(TStage stage, ILifecycleObserver observer);
    }
}
