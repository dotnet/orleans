using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Stages of a grains lifecycle.
    /// TODO: Add more later, see ActivationInitializationStage
    /// Full grain lifecycle, including register, state setup, and 
    ///   stream cleanup should all eventually be triggered by the 
    ///   grain lifecycle.
    /// </summary>
    public enum GrainLifecycleStage
    {
        /// <summary>
        /// Setup grain state prior to activation 
        /// </summary>
        SetupState = 1<<10,  

        /// <summary>
        /// Activate grain
        /// </summary>
        Activate = SetupState + 1<<10,
    }

    public static class GrainLifecycleExtensions
    {
        public static IDisposable Subscribe(this IGrainLifecycle observable, GrainLifecycleStage stage, ILifecycleObserver observer)
        {
            return observable.Subscribe((int)stage, observer);
        }

        public static IDisposable Subscribe(this ILifecycleObservable observable, GrainLifecycleStage stage, Func<CancellationToken, Task> onStart, Func<CancellationToken, Task> onStop)
        {
            return observable.Subscribe((int)stage, onStart, onStop);
        }

        public static IDisposable Subscribe(this ILifecycleObservable observable, GrainLifecycleStage stage, Func<CancellationToken, Task> onStart)
        {
            return observable.Subscribe((int)stage, onStart);
        }
    }
}
