using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Decorator over lifecycle subject for silo.  Adds some logging and monitoring
    /// </summary>
    public class SiloLifecycleSubject : LifecycleSubject, ISiloLifecycleSubject
    {
        private readonly ILogger<SiloLifecycleSubject> logger;
        private readonly List<MonitoredObserver> observers;

        public SiloLifecycleSubject(ILogger<SiloLifecycleSubject> logger)
            :base(logger)
        {
            this.logger = logger;
            this.observers = new List<MonitoredObserver>();
        }

        public override Task OnStart(CancellationToken ct)
        {
            foreach(IGrouping<int,MonitoredObserver> stage in this.observers.GroupBy(o => o.Stage).OrderBy(s => s.Key))
            {
                this.logger?.Info(ErrorCode.LifecycleStagesReport, $"Stage {stage.Key}: {string.Join(", ", stage.Select(o => o.Name))}", stage.Key);
            }
            return base.OnStart(ct);
        }

        protected override void PerfMeasureOnStop(int? stage, TimeSpan timelapsed)
        {
            this.logger?.Info(ErrorCode.SiloStartPerfMeasure, $"Stopping lifecycle stage {stage} took {timelapsed.TotalMilliseconds} Milliseconds");
        }

        protected override void PerfMeasureOnStart(int? stage, TimeSpan timelapsed)
        {
            this.logger?.Info(ErrorCode.SiloStartPerfMeasure, $"Starting lifecycle stage {stage} took {timelapsed.TotalMilliseconds} Milliseconds");
        }

        public override IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            var monitoredObserver = new MonitoredObserver(observerName, stage, observer, this.logger);
            this.observers.Add(monitoredObserver);
            return base.Subscribe(observerName, stage, monitoredObserver);
        }

        private class MonitoredObserver : ILifecycleObserver
        {
            private readonly ILifecycleObserver observer;
            private readonly ILogger<SiloLifecycleSubject> logger;

            public MonitoredObserver(string name, int stage, ILifecycleObserver observer, ILogger<SiloLifecycleSubject> logger)
            {
                this.Name = name;
                this.Stage = stage;
                this.observer = observer;
                this.logger = logger;
            }

            public string Name { get; }
            public int Stage { get; }

            public async Task OnStart(CancellationToken ct)
            {
                try
                {
                    Stopwatch stopWatch = Stopwatch.StartNew();
                    await this.observer.OnStart(ct);
                    stopWatch.Stop();
                    this.logger?.Info(ErrorCode.SiloStartPerfMeasure, $"Lifecycle observer {this.Name} started in stage {this.Stage} which took {stopWatch.ElapsedMilliseconds} Milliseconds.");
                }
                catch (Exception ex)
                {
                    string error = $"Lifecycle observer {this.Name} failed to start due to errors at stage {this.Stage}.";
                    this.logger?.Error(ErrorCode.LifecycleStartFailure, error, ex);
                    throw;
                }
            }

            public async Task OnStop(CancellationToken ct)
            {
                try
                {
                    Stopwatch stopWatch = Stopwatch.StartNew();
                    await this.observer.OnStop(ct);
                    stopWatch.Stop();
                    this.logger?.Info(ErrorCode.SiloStartPerfMeasure, $"Lifecycle observer {this.Name} stopped in stage {this.Stage} which took {stopWatch.ElapsedMilliseconds} Milliseconds.");
                }
                catch (Exception ex)
                {
                    string error = $"Lifecycle observer {this.Name} failed to stop due to errors at stage {this.Stage}.";
                    this.logger?.Error(ErrorCode.LifecycleStartFailure, error, ex);
                    throw;
                }
            }
        }
    }
}
