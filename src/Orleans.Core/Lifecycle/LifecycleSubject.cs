using System;
using System.Collections.Generic;
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
        // There is always the OnActivate and OnDeactivate particpant and very often the storage system.
        private readonly List<(int Stage, ILifecycleObserver Observer)> subscribers = new List<(int Stage, ILifecycleObserver Observer)>(2);
        private readonly ILogger logger;
        private int? highStage = null;

        public LifecycleSubject(ILogger<LifecycleSubject> logger)
        {
            this.logger = logger;
        }

        protected virtual void PerfMeasureOnStart(int? stage, TimeSpan timelapsed)
        {
            if (this.logger != null && this.logger.IsEnabled(LogLevel.Trace))
                this.logger.Trace(ErrorCode.SiloStartPerfMeasure, $"Starting lifecycle stage {stage} took {timelapsed.TotalMilliseconds} Milliseconds");
        }

        public virtual async Task OnStart(CancellationToken ct)
        {
            if (this.highStage.HasValue) throw new InvalidOperationException("Lifecycle has already been started.");
            try
            {
                foreach (var observerGroup in this.subscribers
                    .GroupBy(orderedObserver => orderedObserver.Stage)
                    .OrderBy(group => group.Key))
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw new OrleansLifecycleCanceledException("Lifecycle start canceled by request");
                    }
                    this.highStage = observerGroup.Key;
                    var stopWatch = ValueStopwatch.StartNew();
                    await Task.WhenAll(observerGroup.Select(orderedObserver => WrapExecution(ct, orderedObserver.Observer.OnStart)));
                    stopWatch.Stop();
                    this.PerfMeasureOnStart(this.highStage, stopWatch.Elapsed);
                }
            }
            catch (Exception ex)
            {
                var error = $"Lifecycle start canceled due to errors at stage {this.highStage}";
                this.logger?.Error(ErrorCode.LifecycleStartFailure, error, ex);
                throw new OrleansLifecycleCanceledException(error, ex);
            }
        }

        protected virtual void PerfMeasureOnStop(int? stage, TimeSpan timelapsed)
        {
            if (this.logger != null && this.logger.IsEnabled(LogLevel.Trace))
                this.logger.Trace(ErrorCode.SiloStartPerfMeasure, $"Stopping lifecycle stage {stage} took {timelapsed.TotalMilliseconds} Milliseconds");
        }

        public virtual async Task OnStop(CancellationToken ct)
        {
            // if not started, do nothing
            if (!this.highStage.HasValue) return;
            foreach (var observerGroup in this.subscribers
                .GroupBy(orderedObserver => orderedObserver.Stage)
                .OrderByDescending(group => group.Key)
                // skip all until we hit the highest started stage
                .SkipWhile(group => !this.highStage.Equals(group.Key)))
            {
                this.highStage = observerGroup.Key;
                try
                {
                    var stopWatch = ValueStopwatch.StartNew();
                    await Task.WhenAll(observerGroup.Select(orderedObserver => WrapExecution(ct, orderedObserver.Observer.OnStop)));
                    stopWatch.Stop();
                    this.PerfMeasureOnStop(this.highStage, stopWatch.Elapsed);
                }
                catch (Exception ex)
                {
                    this.logger?.Error(ErrorCode.LifecycleStopFailure, $"Stopping lifecycle encountered an error at stage {this.highStage}. Continuing to stop.", ex);
                }
            }
        }

        public virtual IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            if (this.highStage.HasValue) throw new InvalidOperationException("Lifecycle has already been started.");

            var item = (stage, observer);

            this.subscribers.Add(item);

            return new Disposable(() => this.subscribers.Remove(item));
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
    }
}
