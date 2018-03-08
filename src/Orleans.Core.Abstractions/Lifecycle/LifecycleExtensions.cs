using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans
{
    public static class LifecycleExtensions
    {
        private static Func<CancellationToken, Task> NoOp => ct => Task.CompletedTask;

        public static IDisposable Subscribe(this ILifecycleObservable observable, string observerName, int stage, Func<CancellationToken, Task> onStart, Func<CancellationToken, Task> onStop)
        {
            if (observable == null) throw new ArgumentNullException(nameof(observable));
            if (onStart == null) throw new ArgumentNullException(nameof(onStart));
            if (onStop == null) throw new ArgumentNullException(nameof(onStop));

            return observable.Subscribe(observerName, stage, new Observer(onStart, onStop));
        }

        public static IDisposable Subscribe(this ILifecycleObservable observable, string observerName, int stage, Func<CancellationToken, Task> onStart)
        {
            return observable.Subscribe(observerName, stage, onStart, NoOp);
        }

        public static IDisposable Subscribe<TObserver>(this ILifecycleObservable observable, int stage, ILifecycleObserver observer)
        {
            return observable.Subscribe(typeof(TObserver).FullName, stage, observer);
        }

        public static IDisposable Subscribe<TObserver>(this ILifecycleObservable observable, int stage, Func<CancellationToken, Task> onStart, Func<CancellationToken, Task> onStop)
        {
            return observable.Subscribe(typeof(TObserver).FullName, stage, onStart, onStop);
        }

        public static IDisposable Subscribe<TObserver>(this ILifecycleObservable observable, int stage, Func<CancellationToken, Task> onStart)
        {
            return observable.Subscribe<TObserver>(stage, onStart, NoOp);
        }

        public static IDisposable Subscribe(this ILifecycleObservable observable, int stage, ILifecycleObserver observer)
        {
            return observable.Subscribe(observer.GetType().FullName, stage, observer);
        }

        public static Task OnStart(this ILifecycleObserver observer)
        {
            return observer.OnStart(CancellationToken.None);
        }

        public static Task OnStop(this ILifecycleObserver observer)
        {
            return observer.OnStop(CancellationToken.None);
        }

        private class Observer : ILifecycleObserver
        {
            private readonly Func<CancellationToken, Task> onStart;
            private readonly Func<CancellationToken, Task> onStop;

            public Observer(Func<CancellationToken, Task> onStart, Func<CancellationToken, Task> onStop)
            {
                this.onStart = onStart;
                this.onStop = onStop;
            }

            public Task OnStart(CancellationToken ct) => this.onStart(ct);
            public Task OnStop(CancellationToken ct) => this.onStop(ct);
        }
    }
}
