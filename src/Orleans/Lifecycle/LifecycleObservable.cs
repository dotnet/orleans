using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    public class LifecycleObservable<TStage> : ILifecycleObservable<TStage>, ILifecycleObserver
    {
        private readonly ConcurrentDictionary<object, OrderedObserver> subscribers;
        private readonly Logger logger;
        private TStage highStage;

        public LifecycleObservable(Logger logger)
        {
            this.logger = logger?.GetLogger(GetType().Name);
            subscribers = new ConcurrentDictionary<object, OrderedObserver>();
        }

        public async Task OnStart()
        {
            try
            {
                foreach (IGrouping<TStage, OrderedObserver> observerGroup in subscribers.Values
                    .GroupBy(orderedObserver => orderedObserver.Stage)
                    .OrderBy(group => group.Key))
                {
                    highStage = observerGroup.Key;
                    await Task.WhenAll(observerGroup.Select(orderedObserver => WrapExecution(orderedObserver.Observer.OnStart)));
                }
            }
            catch (Exception ex)
            {
                string error = $"Lifecycle start canceled due to errors at stage {this.highStage}";
                logger?.Error(ErrorCode.LifecycleStartFailure, error, ex);
                throw new OperationCanceledException(error, ex);
            }
        }

        public async Task OnStop()
        {
            bool skip = true;
            foreach (IGrouping<TStage, OrderedObserver> observerGroup in subscribers.Values
                .GroupBy(orderedObserver => orderedObserver.Stage)
                .OrderByDescending(group => group.Key))
            {
                // skip all until we hit the highest started stage
                if (skip && highStage.Equals(observerGroup.Key))
                {
                    skip = false;
                }
                if (skip)
                {
                    continue;
                }
                highStage = observerGroup.Key;
                try
                {
                    await Task.WhenAll(observerGroup.Select(orderedObserver => WrapExecution(orderedObserver.Observer.OnStop)));
                }
                catch (Exception ex)
                {
                    logger?.Error(ErrorCode.LifecycleStopFailure, $"Stopping lifecycle encountered an error at stage {this.highStage}.  Continuing to stop.", ex);
                }
            }
        }

        public IDisposable Subscribe(TStage stage, ILifecycleObserver observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));

            var orderedObserver = new OrderedObserver(stage, observer);
            subscribers.TryAdd(orderedObserver, orderedObserver);
            return new Disposable(() => Remove(orderedObserver));
        }

        private void Remove(object key)
        {
            OrderedObserver o;
            subscribers.TryRemove(key, out o);
        }

        private static async Task WrapExecution(Func<Task> action)
        {
            await action();
        }

        private class Disposable : IDisposable
        {
            private readonly Action dispose;

            public Disposable(Action dispose)
            {
                this.dispose = dispose;
            }

            public void Dispose()
            {
                this.dispose();
            }
        }

        private class OrderedObserver
        {
            public ILifecycleObserver Observer { get; }
            public TStage Stage { get; }

            public OrderedObserver(TStage stage, ILifecycleObserver observer)
            {
                Stage = stage;
                Observer = observer;
            }
        }
    }
}
