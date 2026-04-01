using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Diagnostics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Decorator over lifecycle subject for silo.  Adds some logging and monitoring
    /// </summary>
    public partial class SiloLifecycleSubject : LifecycleSubject, ISiloLifecycleSubject
    {
        private static readonly ImmutableDictionary<int, string> StageNames = GetStageNames(typeof(ServiceLifecycleStage));
        private static readonly DiagnosticListener Listener = new(OrleansLifecycleDiagnostics.SiloLifecycleListenerName);
        private readonly List<MonitoredObserver> observers;
        private readonly SiloAddress? _siloAddress;
        private int highestCompletedStage;
        private int lowestStoppedStage;

        /// <inheritdoc />
        public int HighestCompletedStage => this.highestCompletedStage;

        /// <inheritdoc />
        public int LowestStoppedStage => this.lowestStoppedStage;

        /// <summary>
        /// Initializes a new instance of the <see cref="SiloLifecycleSubject"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="localSiloDetails">Optional local silo details for diagnostic events.</param>
        public SiloLifecycleSubject(ILogger<SiloLifecycleSubject> logger, ILocalSiloDetails? localSiloDetails = null) : base(logger)
        {
            this.observers = new List<MonitoredObserver>();
            this.highestCompletedStage = int.MinValue;
            this.lowestStoppedStage = int.MaxValue;
            _siloAddress = localSiloDetails?.SiloAddress;
        }

        /// <inheritdoc />
        public override Task OnStart(CancellationToken cancellationToken = default)
        {
            foreach (var stage in this.observers.GroupBy(o => o.Stage).OrderBy(s => s.Key))
            {
                LogDebugLifecycleStagesReport(stage.Key, string.Join(", ", stage.Select(o => o.Name)));
            }

            return base.OnStart(cancellationToken);
        }

        /// <inheritdoc />
        protected override void OnStartStageCompleted(int stage)
        {
            Interlocked.Exchange(ref this.highestCompletedStage, stage);
            base.OnStartStageCompleted(stage);
        }

        /// <inheritdoc />
        protected override void OnStopStageCompleted(int stage)
        {
            Interlocked.Exchange(ref this.lowestStoppedStage, stage);
            base.OnStopStageCompleted(stage);
        }

        /// <inheritdoc />
        protected override string GetStageName(int stage)
        {
            if (StageNames.TryGetValue(stage, out var result)) return result;
            return base.GetStageName(stage);
        }

        /// <inheritdoc />
        protected override void PerfMeasureOnStop(int stage, TimeSpan elapsed)
        {
            LogDebugStoppingLifecycleStage(this.GetStageName(stage), elapsed);
            if (Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.StageStopped))
            {
                Listener.Write(OrleansLifecycleDiagnostics.EventNames.StageStopped, new LifecycleStageStoppedEvent(
                    stage, this.GetStageName(stage), _siloAddress, elapsed));
            }
        }

        /// <inheritdoc />
        protected override void PerfMeasureOnStart(int stage, TimeSpan elapsed)
        {
            LogDebugStartingLifecycleStage(this.GetStageName(stage), elapsed);
            if (Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.StageCompleted))
            {
                Listener.Write(OrleansLifecycleDiagnostics.EventNames.StageCompleted, new LifecycleStageCompletedEvent(
                    stage, this.GetStageName(stage), _siloAddress, elapsed));
            }
        }

        /// <inheritdoc />
        public override IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            var monitoredObserver = new MonitoredObserver(observerName, stage, this.GetStageName(stage), observer, this.Logger, _siloAddress);
            this.observers.Add(monitoredObserver);
            return base.Subscribe(observerName, stage, monitoredObserver);
        }

        private partial class MonitoredObserver : ILifecycleObserver
        {
            private readonly ILifecycleObserver observer;
            private readonly ILogger logger;
            private readonly SiloAddress? _siloAddress;

            public MonitoredObserver(string name, int stage, string stageName, ILifecycleObserver observer, ILogger logger, SiloAddress? siloAddress)
            {
                this.Name = name;
                this.Stage = stage;
                this.StageName = stageName;
                this.observer = observer;
                this.logger = logger;
                _siloAddress = siloAddress;
            }

            public string Name { get; }
            public int Stage { get; }
            public string StageName { get; }

            public async Task OnStart(CancellationToken ct)
            {
                try
                {
                    if (Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStarting))
                    {
                        Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverStarting, new LifecycleObserverStartingEvent(
                            this.Name, this.Stage, this.StageName, _siloAddress));
                    }

                    var stopwatch = ValueStopwatch.StartNew();
                    await this.observer.OnStart(ct);
                    stopwatch.Stop();
                    LogDebugObserverStarted(this.Name, this.StageName, stopwatch.Elapsed);

                    if (Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverCompleted))
                    {
                        Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverCompleted, new LifecycleObserverCompletedEvent(
                            this.Name, this.Stage, this.StageName, _siloAddress, stopwatch.Elapsed));
                    }
                }
                catch (Exception exception)
                {
                    LogErrorObserverStartFailure(exception, this.Name, this.StageName);

                    if (Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverFailed))
                    {
                        Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverFailed, new LifecycleObserverFailedEvent(
                            this.Name, this.Stage, this.StageName, _siloAddress, exception, TimeSpan.Zero));
                    }

                    throw;
                }
            }

            public async Task OnStop(CancellationToken cancellationToken = default)
            {
                var stopwatch = ValueStopwatch.StartNew();
                try
                {
                    if (Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStopping))
                    {
                        Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverStopping, new LifecycleObserverStoppingEvent(
                            this.Name, this.Stage, this.StageName, _siloAddress));
                    }

                    LogDebugObserverStopping(this.Name, this.StageName);

                    await this.observer.OnStop(cancellationToken);
                    stopwatch.Stop();
                    if (stopwatch.Elapsed > TimeSpan.FromSeconds(1))
                    {
                        LogObserverStopped(LogLevel.Warning, this.Name, this.StageName, stopwatch.Elapsed);
                    }
                    else
                    {
                        LogObserverStopped(LogLevel.Debug, this.Name, this.StageName, stopwatch.Elapsed);
                    }

                    if (Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStopped))
                    {
                        Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverStopped, new LifecycleObserverStoppedEvent(
                            this.Name, this.Stage, this.StageName, _siloAddress, stopwatch.Elapsed));
                    }
                }
                catch (Exception exception)
                {
                    LogErrorObserverStopFailure(exception, this.Name, this.StageName, stopwatch.Elapsed);

                    if (Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverFailed))
                    {
                        Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverFailed, new LifecycleObserverFailedEvent(
                            this.Name, this.Stage, this.StageName, _siloAddress, exception, stopwatch.Elapsed));
                    }

                    throw;
                }
            }

            [LoggerMessage(
                EventId = (int)ErrorCode.SiloStartPerfMeasure,
                Level = LogLevel.Debug,
                Message = "'{Name}' started in stage '{Stage}' in '{Elapsed}'."
            )]
            private partial void LogDebugObserverStarted(string name, string stage, TimeSpan elapsed);

            [LoggerMessage(
                EventId = (int)ErrorCode.LifecycleStartFailure,
                Level = LogLevel.Error,
                Message = "'{Name}' failed to start due to errors at stage '{Stage}'."
            )]
            private partial void LogErrorObserverStartFailure(Exception exception, string name, string stage);

            [LoggerMessage(
                EventId = (int)ErrorCode.SiloStartPerfMeasure,
                Level = LogLevel.Debug,
                Message = "'{Name}' stopping in stage '{Stage}'."
            )]
            private partial void LogDebugObserverStopping(string name, string stage);

            [LoggerMessage(
                EventId = (int)ErrorCode.SiloStartPerfMeasure,
                Message = "'{Name}' stopped in stage '{Stage}' in '{Elapsed}'."
            )]
            private partial void LogObserverStopped(LogLevel logLevel, string name, string stage, TimeSpan elapsed);

            [LoggerMessage(
                EventId = (int)ErrorCode.LifecycleStartFailure,
                Level = LogLevel.Error,
                Message = "'{Name}' failed to stop due to errors at stage '{Stage}' after '{Elapsed}'."
            )]
            private partial void LogErrorObserverStopFailure(Exception exception, string name, string stage, TimeSpan elapsed);
        }

        [LoggerMessage(
            EventId = (int)ErrorCode.LifecycleStagesReport,
            Level = LogLevel.Debug,
            Message = "Stage {Stage}: {Observers}"
        )]
        private partial void LogDebugLifecycleStagesReport(int stage, string observers);

        [LoggerMessage(
            EventId = (int)ErrorCode.SiloStartPerfMeasure,
            Level = LogLevel.Debug,
            Message = "Stopping lifecycle stage '{Stage}' took '{Elapsed}'."
        )]
        private partial void LogDebugStoppingLifecycleStage(string stage, TimeSpan elapsed);

        [LoggerMessage(
            EventId = (int)ErrorCode.SiloStartPerfMeasure,
            Level = LogLevel.Debug,
            Message = "Starting lifecycle stage '{Stage}' took '{Elapsed}'"
        )]
        private partial void LogDebugStartingLifecycleStage(string stage, TimeSpan elapsed);
    }
}
