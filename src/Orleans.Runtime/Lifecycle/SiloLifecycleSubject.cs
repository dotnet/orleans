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
        private readonly ILogger<SiloLifecycleSubject> logger;
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
            this.logger = logger;
            this.observers = new List<MonitoredObserver>();
            this.highestCompletedStage = int.MinValue;
            this.lowestStoppedStage = int.MaxValue;
        }

        /// <inheritdoc />
        public override Task OnStart(CancellationToken cancellationToken = default)
        {
            foreach(var stage in this.observers.GroupBy(o => o.Stage).OrderBy(s => s.Key))
            {
                this.logger?.LogInformation(
                    (int)ErrorCode.LifecycleStagesReport,
                    "Stage {Stage}: {Observers}",
                    this.GetStageName(stage.Key),
                    string.Join(", ", stage.Select(o => o.Name)));
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
            this.logger?.LogInformation(
                    (int)ErrorCode.SiloStartPerfMeasure,
                    "Stopping lifecycle stage {Stage} took {Elapsed} Milliseconds",
                    this.GetStageName(stage),
                    elapsed.TotalMilliseconds);
        }

        /// <inheritdoc />
        protected override void PerfMeasureOnStart(int stage, TimeSpan elapsed)
        {
            this.logger?.LogInformation(
                (int)ErrorCode.SiloStartPerfMeasure,
                    "Starting lifecycle stage {Stage} took {Elapsed} Milliseconds",
                    this.GetStageName(stage),
                    elapsed.TotalMilliseconds);
        }

        /// <inheritdoc />
        public override IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            var monitoredObserver = new MonitoredObserver(observerName, stage, this.GetStageName(stage), observer, this.logger);
            this.observers.Add(monitoredObserver);
            return base.Subscribe(observerName, stage, monitoredObserver);
        }

        private class MonitoredObserver : ILifecycleObserver
        {
            private readonly ILifecycleObserver observer;
            private readonly ILogger<SiloLifecycleSubject> logger;

            public MonitoredObserver(string name, int stage, string stageName, ILifecycleObserver observer, ILogger<SiloLifecycleSubject> logger)
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
                    this.logger?.LogInformation(
                        (int)ErrorCode.SiloStartPerfMeasure,
                        "{Name} started in stage {Stage} in {Elapsed} Milliseconds",
                        this.Name,
                        this.StageName,
                        stopwatch.Elapsed.TotalMilliseconds);
                }
                catch (Exception exception)
                {
                    this.logger?.LogError(
                        (int)ErrorCode.LifecycleStartFailure,
                        "{Name} failed to start due to errors at stage {Stage}: {Exception}",
                        this.Name,
                        this.StageName,
                        exception);
                    throw;
                }
            }

            public async Task OnStop(CancellationToken cancellationToken = default)
            {
                try
                {
                    var stopwatch = ValueStopwatch.StartNew();
                    await this.observer.OnStop(cancellationToken);
                    stopwatch.Stop();
                    this.logger?.LogInformation(
                        (int)ErrorCode.SiloStartPerfMeasure,
                        "{Name} stopped in stage {Stage} in {Elapsed} Milliseconds.",
                        this.Name,
                        this.StageName,
                        stopwatch.Elapsed.TotalMilliseconds);
                }
                catch (Exception exception)
                {
                    this.logger?.LogError(
                        (int)ErrorCode.LifecycleStartFailure,
                        "{Name} failed to stop due to errors at stage {Stage}: {Exception}",
                        this.Name,
                        this.StageName,
                        exception);
                    throw;
                }
            }
        }
    }
}
