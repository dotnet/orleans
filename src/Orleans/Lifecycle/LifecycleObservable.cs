﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
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
            this.subscribers = new ConcurrentDictionary<object, OrderedObserver>();
        }

        public async Task OnStart(CancellationToken ct)
        {
            try
            {
                foreach (IGrouping<TStage, OrderedObserver> observerGroup in this.subscribers.Values
                    .GroupBy(orderedObserver => orderedObserver.Stage)
                    .OrderBy(group => group.Key))
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }
                    this.highStage = observerGroup.Key;
                    await Task.WhenAll(observerGroup.Select(orderedObserver => WrapExecution(ct, orderedObserver.Observer.OnStart)));
                }
            }
            catch (Exception ex)
            {
                string error = $"Lifecycle start canceled due to errors at stage {this.highStage}";
                this.logger?.Error(ErrorCode.LifecycleStartFailure, error, ex);
                throw new OperationCanceledException(error, ex);
            }
        }

        public async Task OnStop(CancellationToken ct)
        {
            foreach (IGrouping<TStage, OrderedObserver> observerGroup in this.subscribers.Values
                .GroupBy(orderedObserver => orderedObserver.Stage)
                .OrderByDescending(group => group.Key)
                // skip all until we hit the highest started stage
                .SkipWhile(group => !this.highStage.Equals(group.Key)))
            {
                this.highStage = observerGroup.Key;
                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }
                    await Task.WhenAll(observerGroup.Select(orderedObserver => WrapExecution(ct, orderedObserver.Observer.OnStop)));
                }
                catch (Exception ex)
                {
                    this.logger?.Error(ErrorCode.LifecycleStopFailure, $"Stopping lifecycle encountered an error at stage {this.highStage}.  Continuing to stop.", ex);
                }
            }
        }

        public IDisposable Subscribe(TStage stage, ILifecycleObserver observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));

            var orderedObserver = new OrderedObserver(stage, observer);
            this.subscribers.TryAdd(orderedObserver, orderedObserver);
            return new Disposable(() => Remove(orderedObserver));
        }

        private void Remove(object key)
        {
            OrderedObserver o;
            this.subscribers.TryRemove(key, out o);
        }

        private static async Task WrapExecution(CancellationToken ct, Func<CancellationToken, Task> action)
        {
            await action(ct);
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
