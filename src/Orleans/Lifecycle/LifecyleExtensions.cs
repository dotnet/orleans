using System;
using System.Threading.Tasks;

namespace Orleans
{
    public static class LifecyleExtensions
    {
        private static Func<Task> NoOp => () => Task.CompletedTask;

        public static IDisposable Subscribe<TRing>(this ILifecycleObservable<TRing> observable, TRing ring, Func<Task> onStart, Func<Task> onStop)
        {
            if (observable == null) throw new ArgumentNullException(nameof(observable));
            if (onStart == null) throw new ArgumentNullException(nameof(onStart));
            if (onStop == null) throw new ArgumentNullException(nameof(onStop));

            return observable.Subscribe(ring, new Observer(onStart, onStop));
        }

        public static IDisposable Subscribe<TRing>(this ILifecycleObservable<TRing> observable, TRing ring, Func<Task> onStart)
        {
            return observable.Subscribe(ring, new Observer(onStart, NoOp));
        }

        private class Observer : ILifecycleObserver
        {
            private readonly Func<Task> onStart;
            private readonly Func<Task> onStop;

            public Observer(Func<Task> onStart, Func<Task> onStop)
            {
                this.onStart = onStart;
                this.onStop = onStop;
            }

            public Task OnStart() => onStart();
            public Task OnStop() => onStop();
        }
    }

}
