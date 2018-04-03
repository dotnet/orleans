using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Observable lifecycle
    /// Notes:
    /// - Single use, does not support multiple start/stop cycles.
    /// - Once started, no other observers can be subscribed.
    /// - OnStart starts stages in order until first failure or cancelation.
    /// - OnStop stops states in reverse order starting from highest started stage.
    /// - OnStop stops all stages regardless of errors even if canceled canceled.
    /// </summary>
    public class LifecycleSubject : ILifecycleSubject
    {
        private readonly ConcurrentDictionary<object, OrderedObserver> subscribers;
        private readonly ILogger logger;
        private int? highStage = null;

        public LifecycleSubject(ILogger<LifecycleSubject> logger)
        {
            this.logger = logger;
            this.subscribers = new ConcurrentDictionary<object, OrderedObserver>();
        }

        public async Task OnStart(CancellationToken ct)
        {
            if (this.highStage.HasValue) throw new InvalidOperationException("Lifecycle has already been started.");
            try
            {
                foreach (IGrouping<int, OrderedObserver> observerGroup in this.subscribers.Values
                    .GroupBy(orderedObserver => orderedObserver.Stage)
                    .OrderBy(group => group.Key))
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw new OrleansLifecycleCanceledException("Lifecycle start canceled by request");
                    }
                    this.highStage = observerGroup.Key;
                    Stopwatch stopWatch = Stopwatch.StartNew();
                    await Task.WhenAll(observerGroup.Select(orderedObserver => WrapExecution(ct, orderedObserver.Observer.OnStart)));
                    stopWatch.Stop();
                    this.logger?.Info(ErrorCode.SiloStartPerfMeasure, $"Starting lifecycle stage {this.highStage} took {stopWatch.ElapsedMilliseconds} Milliseconds");
                }
            }
            catch (Exception ex)
            {
                string error = $"Lifecycle start canceled due to errors at stage {this.highStage}";
                this.logger?.Error(ErrorCode.LifecycleStartFailure, error, ex);
                throw new OrleansLifecycleCanceledException(error, ex);
            }
        }

        public async Task OnStop(CancellationToken ct)
        {
            // if not started, do nothing
            if (!this.highStage.HasValue) return;
            foreach (IGrouping<int, OrderedObserver> observerGroup in this.subscribers.Values
                .GroupBy(orderedObserver => orderedObserver.Stage)
                .OrderByDescending(group => group.Key)
                // skip all until we hit the highest started stage
                .SkipWhile(group => !this.highStage.Equals(group.Key)))
            {
                this.highStage = observerGroup.Key;
                try
                {
                    Stopwatch stopWatch = Stopwatch.StartNew();
                    await Task.WhenAll(observerGroup.Select(orderedObserver => WrapExecution(ct, orderedObserver.Observer.OnStop)));
                    stopWatch.Stop();
                    this.logger?.Info(ErrorCode.SiloStartPerfMeasure, $"Stopping lifecycle stage {this.highStage} took {stopWatch.ElapsedMilliseconds} Milliseconds");
                }
                catch (Exception ex)
                {
                    this.logger?.Error(ErrorCode.LifecycleStopFailure, $"Stopping lifecycle encountered an error at stage {this.highStage}.  Continuing to stop.", ex);
                }
            }
        }

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            if (this.highStage.HasValue) throw new InvalidOperationException("Lifecycle has already been started.");

            var orderedObserver = new OrderedObserver(stage, observer);
            this.subscribers.TryAdd(orderedObserver, orderedObserver);
            return new Disposable(() => Remove(orderedObserver));
        }

        private void Remove(object key)
        {
            this.subscribers.TryRemove(key, out OrderedObserver o);
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
            public int Stage { get; }

            public OrderedObserver(int stage, ILifecycleObserver observer)
            {
                Stage = stage;
                Observer = observer;
            }
        }
    }
}
