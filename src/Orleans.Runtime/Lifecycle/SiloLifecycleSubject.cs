#nullable enable
using Microsoft.Extensions.Logging;
using Orleans.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Decorator over lifecycle subject for silo.  Adds some logging and monitoring
    /// </summary>
    public partial class SiloLifecycleSubject : LifecycleSubject, ISiloLifecycleSubject
    {
        private static readonly DiagnosticListener DiagnosticListener = new(OrleansLifecycleDiagnostics.SiloLifecycleListenerName);
        private static readonly ImmutableDictionary<int, string> StageNames = GetStageNames(typeof(ServiceLifecycleStage));
        private readonly List<MonitoredObserver> observers;
        private readonly ILocalSiloDetails? localSiloDetails;
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
        public SiloLifecycleSubject(ILogger<SiloLifecycleSubject> logger) : this(logger, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SiloLifecycleSubject"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="localSiloDetails">The local silo details, used for diagnostic events.</param>
        public SiloLifecycleSubject(ILogger<SiloLifecycleSubject> logger, ILocalSiloDetails? localSiloDetails) : base(logger)
        {
            this.observers = new List<MonitoredObserver>();
            this.localSiloDetails = localSiloDetails;
            this.highestCompletedStage = int.MinValue;
            this.lowestStoppedStage = int.MaxValue;
        }

        private SiloAddress? SiloAddress => this.localSiloDetails?.SiloAddress;

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

            if (DiagnosticListener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.StageStopped))
            {
                DiagnosticListener.Write(
                    OrleansLifecycleDiagnostics.EventNames.StageStopped,
                    new LifecycleStageStoppedEvent(stage, GetStageName(stage), SiloAddress, elapsed));
            }
        }

        /// <inheritdoc />
        protected override void PerfMeasureOnStart(int stage, TimeSpan elapsed)
        {
            LogDebugStartingLifecycleStage(this.GetStageName(stage), elapsed);

            if (DiagnosticListener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.StageCompleted))
            {
                DiagnosticListener.Write(
                    OrleansLifecycleDiagnostics.EventNames.StageCompleted,
                    new LifecycleStageCompletedEvent(stage, GetStageName(stage), SiloAddress, elapsed));
            }
        }

        /// <inheritdoc />
        public override IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            var monitoredObserver = new MonitoredObserver(observerName, stage, this.GetStageName(stage), observer, this.Logger, this);
            this.observers.Add(monitoredObserver);
            return base.Subscribe(observerName, stage, monitoredObserver);
        }

        private partial class MonitoredObserver : ILifecycleObserver
        {
            private readonly ILifecycleObserver observer;
            private readonly ILogger logger;
            private readonly SiloLifecycleSubject parent;

            public MonitoredObserver(string name, int stage, string stageName, ILifecycleObserver observer, ILogger logger, SiloLifecycleSubject parent)
            {
                this.Name = name;
                this.Stage = stage;
                this.StageName = stageName;
                this.observer = observer;
                this.logger = logger;
                this.parent = parent;
            }

            public string Name { get; }
            public int Stage { get; }
            public string StageName { get; }

            public async Task OnStart(CancellationToken ct)
            {
                if (DiagnosticListener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStarting))
                {
                    DiagnosticListener.Write(
                        OrleansLifecycleDiagnostics.EventNames.ObserverStarting,
                        new LifecycleObserverStartingEvent(Name, Stage, StageName, parent.SiloAddress));
                }

                try
                {
                    var stopwatch = ValueStopwatch.StartNew();
                    await this.observer.OnStart(ct);
                    stopwatch.Stop();
                    LogDebugObserverStarted(this.Name, this.StageName, stopwatch.Elapsed);

                    if (DiagnosticListener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverCompleted))
                    {
                        DiagnosticListener.Write(
                            OrleansLifecycleDiagnostics.EventNames.ObserverCompleted,
                            new LifecycleObserverCompletedEvent(Name, Stage, StageName, parent.SiloAddress, stopwatch.Elapsed));
                    }
                }
                catch (Exception exception)
                {
                    LogErrorObserverStartFailure(exception, this.Name, this.StageName);

                    if (DiagnosticListener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverFailed))
                    {
                        DiagnosticListener.Write(
                            OrleansLifecycleDiagnostics.EventNames.ObserverFailed,
                            new LifecycleObserverFailedEvent(Name, Stage, StageName, parent.SiloAddress, exception, default));
                    }

                    throw;
                }
            }

            public async Task OnStop(CancellationToken cancellationToken = default)
            {
                if (DiagnosticListener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStopping))
                {
                    DiagnosticListener.Write(
                        OrleansLifecycleDiagnostics.EventNames.ObserverStopping,
                        new LifecycleObserverStoppingEvent(Name, Stage, StageName, parent.SiloAddress));
                }

                var stopwatch = ValueStopwatch.StartNew();
                try
                {
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

                    if (DiagnosticListener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStopped))
                    {
                        DiagnosticListener.Write(
                            OrleansLifecycleDiagnostics.EventNames.ObserverStopped,
                            new LifecycleObserverStoppedEvent(Name, Stage, StageName, parent.SiloAddress, stopwatch.Elapsed));
                    }
                }
                catch (Exception exception)
                {
                    LogErrorObserverStopFailure(exception, this.Name, this.StageName, stopwatch.Elapsed);

                    if (DiagnosticListener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverFailed))
                    {
                        DiagnosticListener.Write(
                            OrleansLifecycleDiagnostics.EventNames.ObserverFailed,
                            new LifecycleObserverFailedEvent(Name, Stage, StageName, parent.SiloAddress, exception, stopwatch.Elapsed));
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
