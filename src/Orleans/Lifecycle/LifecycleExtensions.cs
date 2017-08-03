using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans
{
    public static class LifecycleExtensions
    {
        private static Func<CancellationTokenSource, Task> NoOp => cts => Task.CompletedTask;

        public static IDisposable Subscribe<TStage>(this ILifecycleObservable<TStage> observable, TStage stage, Func<CancellationTokenSource, Task> onStart, Func<CancellationTokenSource, Task> onStop)
        {
            if (observable == null) throw new ArgumentNullException(nameof(observable));
            if (onStart == null) throw new ArgumentNullException(nameof(onStart));
            if (onStop == null) throw new ArgumentNullException(nameof(onStop));

            return observable.Subscribe(stage, new Observer(onStart, onStop));
        }

        public static IDisposable Subscribe<TStage>(this ILifecycleObservable<TStage> observable, TStage stage, Func<CancellationTokenSource, Task> onStart)
        {
            return observable.Subscribe(stage, new Observer(onStart, NoOp));
        }

        private class Observer : ILifecycleObserver
        {
            private readonly Func<CancellationTokenSource, Task> onStart;
            private readonly Func<CancellationTokenSource, Task> onStop;

            public Observer(Func<CancellationTokenSource, Task> onStart, Func<CancellationTokenSource, Task> onStop)
            {
                this.onStart = onStart;
                this.onStop = onStop;
            }

            public Task OnStart(CancellationTokenSource cts = null) => onStart(cts);
            public Task OnStop(CancellationTokenSource cts = null) => onStop(cts);
        }
    }

}
