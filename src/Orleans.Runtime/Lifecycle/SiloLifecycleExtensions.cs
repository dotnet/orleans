using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    public static class SiloLifecycleExtensions
    {
        public static IDisposable Subscribe(this ISiloLifecycle observable, SiloLifecycleStage stage, ILifecycleObserver observer)
        {
            return observable.Subscribe((int)stage, observer);
        }

        public static IDisposable Subscribe(this ISiloLifecycle observable, SiloLifecycleStage stage, Func<CancellationToken, Task> onStart, Func<CancellationToken, Task> onStop)
        {
            return observable.Subscribe((int)stage, onStart, onStop);
        }

        public static IDisposable Subscribe(this ISiloLifecycle observable, SiloLifecycleStage stage, Func<CancellationToken, Task> onStart)
        {
            return observable.Subscribe((int)stage, onStart);
        }
    }
}
