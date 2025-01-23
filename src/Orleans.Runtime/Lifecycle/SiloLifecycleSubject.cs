using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Decorator over lifecycle subject for silo.  Adds some logging and monitoring
    /// </summary>
    public class SiloLifecycleSubject : LifecycleSubject, ISiloLifecycleSubject
    {
        private static readonly ImmutableDictionary<int, string> StageNames = GetStageNames(typeof(ServiceLifecycleStage));
        private readonly List<MonitoredObserver> observers;
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
        public SiloLifecycleSubject(ILogger<SiloLifecycleSubject> logger) : base(logger)
        {
            this.observers = new List<MonitoredObserver>();
            this.highestCompletedStage = int.MinValue;
            this.lowestStoppedStage = int.MaxValue;
        }

        /// <inheritdoc />
        public override Task OnStart(CancellationToken cancellationToken = default)
        {
            foreach (var stage in this.observers.GroupBy(o => o.Stage).OrderBy(s => s.Key))
            {
                if (this.Logger.IsEnabled(LogLevel.Debug))
                {
                    this.Logger.LogDebug(
                        (int)ErrorCode.LifecycleStagesReport,
                        "Stage {Stage}: {Observers}",
                        this.GetStageName(stage.Key),
                        string.Join(", ", stage.Select(o => o.Name)));
                }
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
            SiloLifecycleSubjectLoggerMessages.PerfMeasureOnStop(this.Logger, this.GetStageName(stage), elapsed);
        }

        /// <inheritdoc />
        protected override void PerfMeasureOnStart(int stage, TimeSpan elapsed)
        {
            SiloLifecycleSubjectLoggerMessages.PerfMeasureOnStart(this.Logger, this.GetStageName(stage), elapsed);
        }

        /// <inheritdoc />
        public override IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            var monitoredObserver = new MonitoredObserver(observerName, stage, this.GetStageName(stage), observer, this.Logger);
            this.observers.Add(monitoredObserver);
            return base.Subscribe(observerName, stage, monitoredObserver);
        }

        private class MonitoredObserver : ILifecycleObserver
        {
            private readonly ILifecycleObserver observer;
            private readonly ILogger logger;

            public MonitoredObserver(string name, int stage, string stageName, ILifecycleObserver observer, ILogger logger)
            {
                this.Name = name;
                this.Stage = stage;
                this.StageName = stageName;
                this.observer = observer;
                this.logger = logger;
            }

            public string Name { get; }
            public int Stage { get; }
            public string StageName { get; }

            public async Task OnStart(CancellationToken ct)
            {
                try
                {
                    var stopwatch = ValueStopwatch.StartNew();
                    await this.observer.OnStart(ct);
                    stopwatch.Stop();
                    SiloLifecycleSubjectLoggerMessages.MonitoredObserverOnStart(this.logger, this.Name, this.StageName, stopwatch.Elapsed);
                }
                catch (Exception exception)
                {
                    SiloLifecycleSubjectLoggerMessages.MonitoredObserverOnStartError(this.logger, exception, this.Name, this.StageName);
                    throw;
                }
            }

            public async Task OnStop(CancellationToken cancellationToken = default)
            {
                var stopwatch = ValueStopwatch.StartNew();
                try
                {
                    SiloLifecycleSubjectLoggerMessages.MonitoredObserverOnStop(this.logger, this.Name, this.StageName);
                    await this.observer.OnStop(cancellationToken);
                    stopwatch.Stop();
                    if (stopwatch.Elapsed > TimeSpan.FromSeconds(1))
                    {
                        SiloLifecycleSubjectLoggerMessages.MonitoredObserverOnStopWarning(this.logger, this.Name, this.StageName, stopwatch.Elapsed);
                    }
                    else
                    {
                        SiloLifecycleSubjectLoggerMessages.MonitoredObserverOnStopDebug(this.logger, this.Name, this.StageName, stopwatch.Elapsed);
                    }
                }
                catch (Exception exception)
                {
                    SiloLifecycleSubjectLoggerMessages.MonitoredObserverOnStopError(this.logger, exception, this.Name, this.StageName, stopwatch.Elapsed);
                    throw;
                }
            }
        }
    }

    internal static partial class SiloLifecycleSubjectLoggerMessages
    {
        [LoggerMessage(1, LogLevel.Debug, "Stopping lifecycle stage '{Stage}' took '{Elapsed}'.")]
        public static partial void PerfMeasureOnStop(ILogger logger, string Stage, TimeSpan Elapsed);

        [LoggerMessage(2, LogLevel.Debug, "Starting lifecycle stage '{Stage}' took '{Elapsed}'")]
        public static partial void PerfMeasureOnStart(ILogger logger, string Stage, TimeSpan Elapsed);

        [LoggerMessage(3, LogLevel.Debug, "'{Name}' started in stage '{Stage}' in '{Elapsed}'.")]
        public static partial void MonitoredObserverOnStart(ILogger logger, string Name, string Stage, TimeSpan Elapsed);

        [LoggerMessage(4, LogLevel.Error, "'{Name}' failed to start due to errors at stage '{Stage}'.")]
        public static partial void MonitoredObserverOnStartError(ILogger logger, Exception exception, string Name, string Stage);

        [LoggerMessage(5, LogLevel.Debug, "'{Name}' stopping in stage '{Stage}'.")]
        public static partial void MonitoredObserverOnStop(ILogger logger, string Name, string Stage);

        [LoggerMessage(6, LogLevel.Warning, "'{Name}' stopped in stage '{Stage}' in '{Elapsed}'.")]
        public static partial void MonitoredObserverOnStopWarning(ILogger logger, string Name, string Stage, TimeSpan Elapsed);

        [LoggerMessage(7, LogLevel.Debug, "'{Name}' stopped in stage '{Stage}' in '{Elapsed}'.")]
        public static partial void MonitoredObserverOnStopDebug(ILogger logger, string Name, string Stage, TimeSpan Elapsed);

        [LoggerMessage(8, LogLevel.Error, "'{Name}' failed to stop due to errors at stage '{Stage}' after '{Elapsed}'.")]
        public static partial void MonitoredObserverOnStopError(ILogger logger, Exception exception, string Name, string Stage, TimeSpan Elapsed);
    }
}
