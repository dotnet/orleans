using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans
{
    public static class LifecycleExtensions
    {
        public static IDisposable Subscribe(this ILifecycleObservable observable, string observerName, int stage, Func<CancellationToken, Task> onStart, Func<CancellationToken, Task> onStop)
        {
            if (observable is null) throw new ArgumentNullException(nameof(observable));
            if (onStart is null) throw new ArgumentNullException(nameof(onStart));

            if (onStop is null)
            {
                var observer = new StartupObserver(onStart);
                return observer.Registration = observable.Subscribe(observerName, stage, observer);
            }

            return observable.Subscribe(observerName, stage, new Observer(onStart, onStop));
        }

        public static IDisposable Subscribe(this ILifecycleObservable observable, string observerName, int stage, Func<CancellationToken, Task> onStart)
        {
            return observable.Subscribe(observerName, stage, onStart, null);
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
            return observable.Subscribe(typeof(TObserver).FullName, stage, onStart, null);
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

        private sealed class StartupObserver : ILifecycleObserver
        {
            private readonly Func<CancellationToken, Task> onStart;
            public IDisposable Registration;

            public StartupObserver(Func<CancellationToken, Task> onStart) => this.onStart = onStart;

            public Task OnStart(CancellationToken ct)
            {
                var task = this.onStart(ct);
                Registration?.Dispose();
                return task;
            }

            public Task OnStop(CancellationToken ct) => Task.CompletedTask;
        }
    }
}
