using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
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

        protected virtual string GetStageName(int stage) => stage.ToString();

        protected static ImmutableDictionary<int, string> GetStageNames(Type type)
        {
            try
            {
                var result = ImmutableDictionary.CreateBuilder<int, string>();
                var fields = type.GetFields(
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                foreach (var field in fields)
                {
                    if (typeof(int).IsAssignableFrom(field.FieldType))
                    {
                        try
                        {
                            var value = (int)field.GetValue(null);
                            result[value] = $"{field.Name} ({value})";
                        }
                        catch
                        {
                            // Ignore.
                        }
                    }
                }

                return result.ToImmutable();
            }
            catch
            {
                return ImmutableDictionary<int, string>.Empty;
            }
        }

        protected virtual void PerfMeasureOnStart(int stage, TimeSpan elapsed)
        {
            if (this.logger != null && this.logger.IsEnabled(LogLevel.Trace))
            {
                this.logger.LogTrace(
                    (int)ErrorCode.SiloStartPerfMeasure,
                    "Starting lifecycle stage {Stage} took {Elapsed} Milliseconds",
                    stage,
                    elapsed.TotalMilliseconds);
            }
        }

        public virtual async Task OnStart(CancellationToken ct)
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

                    var stage = observerGroup.Key;
                    this.highStage = stage;
                    var stopWatch = ValueStopwatch.StartNew();
                    await Task.WhenAll(observerGroup.Select(orderedObserver => CallOnStart(observerGroup.Key, orderedObserver, ct)));
                    stopWatch.Stop();

                    this.OnStartStageCompleted(stage);
                }
            }
            catch (Exception ex) when (!(ex is OrleansLifecycleCanceledException))
            {
                this.logger?.LogError(
                    (int)ErrorCode.LifecycleStartFailure,
                    "Lifecycle start canceled due to errors at stage {Stage}: {Exception}",
                    this.highStage,
                    ex);
                throw;
            }

            async Task CallOnStart(int stage, OrderedObserver observer, CancellationToken cancellationToken)
            {
                await observer.Observer.OnStart(cancellationToken);
            }
        }
        protected virtual void OnStartStageCompleted(int stage) { }
        
        protected virtual void PerfMeasureOnStop(int stage, TimeSpan elapsed)
        {
            if (this.logger != null && this.logger.IsEnabled(LogLevel.Trace))
            {
                this.logger.LogTrace(
                    (int)ErrorCode.SiloStartPerfMeasure,
                    "Stopping lifecycle stage {Stage} took {Elapsed} Milliseconds",
                    stage,
                    elapsed.TotalMilliseconds);
            }
        }

        public virtual async Task OnStop(CancellationToken ct)
        {
            // if not started, do nothing
            if (!this.highStage.HasValue) return;
            var loggedCancellation = false;
            foreach (IGrouping<int, OrderedObserver> observerGroup in this.subscribers.Values
                .GroupBy(orderedObserver => orderedObserver.Stage)
                .OrderByDescending(group => group.Key)
                // skip all until we hit the highest started stage
                .SkipWhile(group => !this.highStage.Equals(group.Key)))
            {
                if (ct.IsCancellationRequested && !loggedCancellation)
                {
                    this.logger?.LogWarning("Lifecycle stop operations canceled by request.");
                    loggedCancellation = true;
                }

                var stage = observerGroup.Key;
                this.highStage = stage;
                try
                {
                    var stopwatch = ValueStopwatch.StartNew();
                    await Task.WhenAll(observerGroup.Select(orderedObserver => CallOnStop(observerGroup.Key, orderedObserver, ct)));
                    stopwatch.Stop();
                    this.PerfMeasureOnStop(stage, stopwatch.Elapsed);
                }
                catch (Exception ex)
                {
                    this.logger?.LogError(
                        (int)ErrorCode.LifecycleStopFailure,
                        "Stopping lifecycle encountered an error at stage {Stage}. Continuing to stop. Exception: {Exception}", this.highStage, ex);
                }

                this.OnStopStageCompleted(stage);
            }

            async Task CallOnStop(int stage, OrderedObserver observer, CancellationToken cancellationToken)
            {
                await observer.Observer.OnStop(cancellationToken);
            }
        }

        protected virtual void OnStopStageCompleted(int stage) { }

        public virtual IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            if (this.highStage.HasValue) throw new InvalidOperationException("Lifecycle has already been started.");

            var orderedObserver = new OrderedObserver(stage, observer);
            this.subscribers.TryAdd(orderedObserver, orderedObserver);
            return new Disposable(() => this.subscribers.TryRemove(orderedObserver, out _));
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
                this.Stage = stage;
                this.Observer = observer;
            }
        }
    }
}
