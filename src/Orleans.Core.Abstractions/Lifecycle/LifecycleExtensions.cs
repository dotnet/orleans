using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// Extensions for working with lifecycle observers.
    /// </summary>
    public static class LifecycleExtensions
    {
        /// <summary>
        /// Creates a disposable subscription to the lifecycle.
        /// </summary>
        /// <param name="observable">The lifecycle observable.</param>
        /// <param name="observerName">The name of the observer.</param>
        /// <param name="stage">The stage to participate in.</param>
        /// <param name="onStart">The delegate called when starting the specified lifecycle stage.</param>
        /// <param name="onStop">Teh delegate to be called when stopping the specified lifecycle stage.</param>
        /// <returns>A <see cref="IDisposable"/> instance which can be disposed to unsubscribe the observer from the lifecycle.</returns>
        public static IDisposable Subscribe(this ILifecycleObservable observable, string observerName, int stage, Func<CancellationToken, Task> onStart, Func<CancellationToken, Task> onStop)
        {
            if (observable is null)
            {
                throw new ArgumentNullException(nameof(observable));
            }

            if (onStart is null)
            {
                throw new ArgumentNullException(nameof(onStart));
            }

            if (onStop is null)
            {
                return StartupObserver.Create(observable, observerName, stage, onStart);
            }

            return observable.Subscribe(observerName, stage, new Observer(onStart, onStop));
        }

        /// <summary>
        /// Creates a disposable subscription to the lifecycle.
        /// </summary>
        /// <param name="observable">The lifecycle observable.</param>
        /// <param name="observerName">The name of the observer.</param>
        /// <param name="stage">The stage to participate in.</param>
        /// <param name="onStart">The delegate called when starting the specified lifecycle stage.</param>
        /// <returns>A <see cref="IDisposable"/> instance which can be disposed to unsubscribe the observer from the lifecycle.</returns>
        public static IDisposable Subscribe(this ILifecycleObservable observable, string observerName, int stage, Func<CancellationToken, Task> onStart)
        {
            return observable.Subscribe(observerName, stage, onStart, null);
        }

        /// <summary>
        /// Creates a disposable subscription to the lifecycle.
        /// </summary>
        /// <typeparam name="TObserver">
        /// The observer type, used for diagnostics.
        /// </typeparam>
        /// <param name="observable">The lifecycle observable.</param>
        /// <param name="stage">The stage to participate in.</param>
        /// <param name="observer">The observer.</param>
        /// <returns>A <see cref="IDisposable"/> instance which can be disposed to unsubscribe the observer from the lifecycle.</returns>
        public static IDisposable Subscribe<TObserver>(this ILifecycleObservable observable, int stage, ILifecycleObserver observer)
        {
            return observable.Subscribe(typeof(TObserver).FullName, stage, observer);
        }

        /// <summary>
        /// Creates a disposable subscription to the lifecycle.
        /// </summary>
        /// <typeparam name="TObserver">
        /// The observer type, used for diagnostics.
        /// </typeparam>
        /// <param name="observable">The lifecycle observable.</param>
        /// <param name="stage">The stage to participate in.</param>
        /// <param name="onStart">The delegate called when starting the specified lifecycle stage.</param>
        /// <param name="onStop">Teh delegate to be called when stopping the specified lifecycle stage.</param>
        /// <returns>A <see cref="IDisposable"/> instance which can be disposed to unsubscribe the observer from the lifecycle.</returns>
        public static IDisposable Subscribe<TObserver>(this ILifecycleObservable observable, int stage, Func<CancellationToken, Task> onStart, Func<CancellationToken, Task> onStop)
        {
            return observable.Subscribe(typeof(TObserver).FullName, stage, onStart, onStop);
        }

        /// <summary>
        /// Creates a disposable subscription to the lifecycle.
        /// </summary>
        /// <typeparam name="TObserver">
        /// The observer type, used for diagnostics.
        /// </typeparam>
        /// <param name="observable">The lifecycle observable.</param>
        /// <param name="stage">The stage to participate in.</param>
        /// <param name="onStart">The delegate called when starting the specified lifecycle stage.</param>
        /// <returns>A <see cref="IDisposable"/> instance which can be disposed to unsubscribe the observer from the lifecycle.</returns>
        public static IDisposable Subscribe<TObserver>(this ILifecycleObservable observable, int stage, Func<CancellationToken, Task> onStart)
        {
            return observable.Subscribe(typeof(TObserver).FullName, stage, onStart, null);
        }

        /// <summary>
        /// Creates a disposable subscription to the lifecycle.
        /// </summary>
        /// <param name="observable">The lifecycle observable.</param>
        /// <param name="stage">The stage to participate in.</param>
        /// <param name="observer">The observer.</param>
        /// <returns>A <see cref="IDisposable"/> instance which can be disposed to unsubscribe the observer from the lifecycle.</returns>
        public static IDisposable Subscribe(this ILifecycleObservable observable, int stage, ILifecycleObserver observer)
        {
            return observable.Subscribe(observer.GetType().FullName, stage, observer);
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
            private readonly Func<CancellationToken, Task> _onStart;
            private readonly IDisposable _registration;

            private StartupObserver(ILifecycleObservable observable, string observerName, int stage, Func<CancellationToken, Task> onStart)
            {
                _onStart = onStart;
                _registration = observable.Subscribe(observerName, stage, this);
            }

            public static IDisposable Create(ILifecycleObservable observable, string observerName, int stage, Func<CancellationToken, Task> onStart)
            {
                var observer = new StartupObserver(observable, observerName, stage, onStart);
                return observer._registration;
            }

            public Task OnStart(CancellationToken ct)
            {
                var task = _onStart(ct);
                _registration?.Dispose();
                return task;
            }

            public Task OnStop(CancellationToken ct) => Task.CompletedTask;
        }
    }
}
