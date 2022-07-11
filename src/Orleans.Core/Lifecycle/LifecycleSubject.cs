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
    /// Provides functionality for observing a lifecycle.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Single use, does not support multiple start/stop cycles.</description></item>
    /// <item><description>Once started, no other observers can be subscribed.</description></item>
    /// <item><description>OnStart starts stages in order until first failure or cancellation.</description></item>
    /// <item><description>OnStop stops states in reverse order starting from highest started stage.</description></item>
    /// <item><description>OnStop stops all stages regardless of errors even if canceled canceled.</description></item>
    /// </list>
    /// </remarks>
    public abstract class LifecycleSubject : ILifecycleSubject
    {
        private readonly List<OrderedObserver> subscribers;
        protected readonly ILogger logger;
        private int? highStage = null;

        protected LifecycleSubject(ILogger logger)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            this.logger = logger;
            this.subscribers = new List<OrderedObserver>();
        }

        /// <summary>
        /// Gets the name of the specified numeric stage.
        /// </summary>
        /// <param name="stage">The stage number.</param>
        /// <returns>The name of the stage.</returns>
        protected virtual string GetStageName(int stage) => stage.ToString();

        /// <summary>
        /// Gets the collection of all stage numbers and their corresponding names.
        /// </summary>
        /// <seealso cref="ServiceLifecycleStage"/>
        /// <param name="type">The lifecycle stage class.</param>
        /// <returns>The collection of all stage numbers and their corresponding names.</returns>
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

        /// <summary>
        /// Logs the observed performance of an <see cref="OnStart"/> call.
        /// </summary>
        /// <param name="stage">The stage.</param>
        /// <param name="elapsed">The period of time which elapsed before <see cref="OnStart"/> completed once it was initiated.</param>
        protected virtual void PerfMeasureOnStart(int stage, TimeSpan elapsed)
        {
            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                this.logger.LogTrace(
                    (int)ErrorCode.SiloStartPerfMeasure,
                    "Starting lifecycle stage {Stage} took {Elapsed} Milliseconds",
                    stage,
                    elapsed.TotalMilliseconds);
            }
        }

        /// <inheritdoc />
        public virtual async Task OnStart(CancellationToken cancellationToken = default)
        {
            if (this.highStage.HasValue) throw new InvalidOperationException("Lifecycle has already been started.");
            try
            {
                foreach (IGrouping<int, OrderedObserver> observerGroup in this.subscribers
                    .GroupBy(orderedObserver => orderedObserver.Stage)
                    .OrderBy(group => group.Key))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OrleansLifecycleCanceledException("Lifecycle start canceled by request");
                    }

                    var stage = observerGroup.Key;
                    this.highStage = stage;
                    var stopWatch = ValueStopwatch.StartNew();
                    await Task.WhenAll(observerGroup.Select(orderedObserver => CallOnStart(orderedObserver, cancellationToken)));
                    stopWatch.Stop();
                    this.PerfMeasureOnStart(stage, stopWatch.Elapsed);

                    this.OnStartStageCompleted(stage);
                }
            }
            catch (Exception ex) when (ex is not OrleansLifecycleCanceledException)
            {
                this.logger.LogError(
                    (int)ErrorCode.LifecycleStartFailure,
                    ex,
                    "Lifecycle start canceled due to errors at stage {Stage}",
                    this.highStage);
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

        /// <summary>
        /// Signifies that <see cref="OnStart"/> completed.
        /// </summary>
        /// <param name="stage">The stage which completed.</param>
        protected virtual void OnStartStageCompleted(int stage) { }

        /// <summary>
        /// Logs the observed performance of an <see cref="OnStop"/> call.
        /// </summary>
        /// <param name="stage">The stage.</param>
        /// <param name="elapsed">The period of time which elapsed before <see cref="OnStop"/> completed once it was initiated.</param>
        protected virtual void PerfMeasureOnStop(int stage, TimeSpan elapsed)
        {
            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                this.logger.LogTrace(
                    (int)ErrorCode.SiloStartPerfMeasure,
                    "Stopping lifecycle stage {Stage} took {Elapsed} Milliseconds",
                    stage,
                    elapsed.TotalMilliseconds);
            }
        }

        /// <inheritdoc />
        public virtual async Task OnStop(CancellationToken cancellationToken = default)
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
                if (cancellationToken.IsCancellationRequested && !loggedCancellation)
                {
                    this.logger.LogWarning("Lifecycle stop operations canceled by request.");
                    loggedCancellation = true;
                }

                var stage = observerGroup.Key;
                this.highStage = stage;
                try
                {
                    var stopwatch = ValueStopwatch.StartNew();
                    await Task.WhenAll(observerGroup.Select(orderedObserver => CallOnStop(orderedObserver, cancellationToken)));
                    stopwatch.Stop();
                    this.PerfMeasureOnStop(stage, stopwatch.Elapsed);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(
                        (int)ErrorCode.LifecycleStopFailure,
                        ex,
                        "Stopping lifecycle encountered an error at stage {Stage}. Continuing to stop.",
                        this.highStage);
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

        /// <summary>
        /// Signifies that <see cref="OnStop"/> completed.
        /// </summary>
        /// <param name="stage">The stage which completed.</param>
        protected virtual void OnStopStageCompleted(int stage) { }

        public virtual IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            if (this.highStage.HasValue) throw new InvalidOperationException("Lifecycle has already been started.");

            var orderedObserver = new OrderedObserver(stage, observer);
            this.subscribers.Add(orderedObserver);
            return orderedObserver;
        }

        /// <summary>
        /// Represents a <see cref="ILifecycleObservable"/>'s participation in a given lifecycle stage.
        /// </summary>
        private class OrderedObserver : IDisposable
        {
            /// <summary>
            /// Gets the observer.
            /// </summary>
            public ILifecycleObserver Observer { get; private set; }

            /// <summary>
            /// Gets the stage which the observer is participating in.
            /// </summary>
            public int Stage { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="OrderedObserver"/> class.
            /// </summary>
            /// <param name="stage">The stage which the observer is participating in.</param>
            /// <param name="observer">The participating observer.</param>
            public OrderedObserver(int stage, ILifecycleObserver observer)
            {
                this.Stage = stage;
                this.Observer = observer;
            }

            /// <inheritdoc />
            public void Dispose() => Observer = null;
        }
    }
}
