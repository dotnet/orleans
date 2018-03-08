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
    public class SiloLifecycleSubject : ISiloLifecycleSubject
    {
        private readonly ILifecycleSubject subject;
        private readonly ILogger<SiloLifecycleSubject> logger;
        private readonly List<MonitoredObserver> observers;

        public SiloLifecycleSubject(ILifecycleSubject subject, ILogger<SiloLifecycleSubject> logger)
        {
            this.subject = subject;
            this.logger = logger;
            this.observers = new List<MonitoredObserver>();
        }

        public Task OnStart(CancellationToken ct)
        {
            foreach(IGrouping<int,MonitoredObserver> stage in this.observers.GroupBy(o => o.Stage).OrderBy(s => s.Key))
            {
                this.logger?.Info(ErrorCode.LifecycleStagesReport, $"Stage {stage.Key}: {string.Join(", ", stage.Select(o => o.Name))}", stage.Key);
            }
            return this.subject.OnStart(ct);
        }

        public Task OnStop(CancellationToken ct)
        {
            return this.subject.OnStop(ct);
        }

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            var monitoredObserver = new MonitoredObserver(observerName, stage, observer, this.logger);
            this.observers.Add(monitoredObserver);
            return this.subject.Subscribe(observerName, stage, monitoredObserver);
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
