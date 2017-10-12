using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{

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
