using System;
using System.Collections.Generic;
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
    public abstract class LifecycleSubject : ILifecycleSubject
    {
        private readonly List<OrderedObserver> subscribers;
        private readonly ILogger logger;
        private int? highStage = null;

        protected LifecycleSubject(ILogger logger)
        {
            this.logger = logger;
            this.subscribers = new List<OrderedObserver>();
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
                foreach (IGrouping<int, OrderedObserver> observerGroup in this.subscribers
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
                    await Task.WhenAll(observerGroup.Select(orderedObserver => CallOnStart(orderedObserver, ct)));
                    stopWatch.Stop();
                    this.PerfMeasureOnStart(stage, stopWatch.Elapsed);

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

            static Task CallOnStart(OrderedObserver observer, CancellationToken cancellationToken)
            {
                try
                {
                    return observer.Observer?.OnStart(cancellationToken) ?? Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    return Task.FromException(ex);
                }
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
            foreach (IGrouping<int, OrderedObserver> observerGroup in this.subscribers
                // include up to highest started stage
                .Where(orderedObserver => orderedObserver.Stage <= highStage && orderedObserver.Observer != null)
                .GroupBy(orderedObserver => orderedObserver.Stage)
                .OrderByDescending(group => group.Key))
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
                    await Task.WhenAll(observerGroup.Select(orderedObserver => CallOnStop(orderedObserver, ct)));
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

            static Task CallOnStop(OrderedObserver observer, CancellationToken cancellationToken)
            {
                try
                {
                    return observer.Observer?.OnStop(cancellationToken) ?? Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    return Task.FromException(ex);
                }
            }
        }

        protected virtual void OnStopStageCompleted(int stage) { }

        public virtual IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            if (this.highStage.HasValue) throw new InvalidOperationException("Lifecycle has already been started.");

            var orderedObserver = new OrderedObserver(stage, observer);
            this.subscribers.Add(orderedObserver);
            return orderedObserver;
        }

        private class OrderedObserver : IDisposable
        {
            public ILifecycleObserver Observer { get; private set; }
            public int Stage { get; }

            public OrderedObserver(int stage, ILifecycleObserver observer)
            {
                this.Stage = stage;
                this.Observer = observer;
            }

            public void Dispose() => Observer = null;
        }
    }
}
