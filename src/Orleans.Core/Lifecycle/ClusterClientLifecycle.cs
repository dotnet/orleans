
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Core.Diagnostics;

namespace Orleans
{
    /// <summary>
    /// Implementation of <see cref="IClusterClientLifecycle"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="ClusterClientLifecycle"/> class.
    /// </remarks>
    /// <param name="logger">The logger.</param>
    /// <param name="localClientDetails">Optional local client details for diagnostic events.</param>
    internal class ClusterClientLifecycle(ILogger logger, LocalClientDetails localClientDetails) : LifecycleSubject(logger), IClusterClientLifecycle
    {
        private static readonly ImmutableDictionary<int, string> StageNames = GetStageNames(typeof(ServiceLifecycleStage));
        private readonly SiloAddress _clientAddress = localClientDetails.ClientAddress;

        /// <inheritdoc />
        protected override string GetStageName(int stage)
        {
            if (StageNames.TryGetValue(stage, out var result))
            {
                return result;
            }

            return base.GetStageName(stage);
        }

        /// <inheritdoc />
        protected override void PerfMeasureOnStart(int stage, TimeSpan elapsed)
        {
            base.PerfMeasureOnStart(stage, elapsed);
            ClientLifecycleEvents.EmitStageCompleted(stage, GetStageName(stage), _clientAddress, elapsed, this);
        }

        /// <inheritdoc />
        protected override void PerfMeasureOnStop(int stage, TimeSpan elapsed)
        {
            base.PerfMeasureOnStop(stage, elapsed);
            ClientLifecycleEvents.EmitStageStopped(stage, GetStageName(stage), _clientAddress, elapsed, this);
        }

        /// <inheritdoc />
        public override IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            return base.Subscribe(observerName, stage, new MonitoredObserver(observerName, stage, GetStageName(stage), observer, _clientAddress));
        }

        private sealed class MonitoredObserver(
            string name,
            int stage,
            string stageName,
            ILifecycleObserver observer,
            SiloAddress clientAddress) : ILifecycleObserver
        {
            public async Task OnStart(CancellationToken cancellationToken)
            {
                var stopwatch = ValueStopwatch.StartNew();
                try
                {
                    ClientLifecycleEvents.EmitObserverStarting(name, stage, stageName, clientAddress, observer);
                    await observer.OnStart(cancellationToken);
                    ClientLifecycleEvents.EmitObserverCompleted(name, stage, stageName, clientAddress, stopwatch.Elapsed, observer);
                }
                catch (Exception exception)
                {
                    ClientLifecycleEvents.EmitObserverFailed(name, stage, stageName, clientAddress, exception, stopwatch.Elapsed, observer);
                    throw;
                }
            }

            public async Task OnStop(CancellationToken cancellationToken = default)
            {
                var stopwatch = ValueStopwatch.StartNew();
                try
                {
                    ClientLifecycleEvents.EmitObserverStopping(name, stage, stageName, clientAddress, observer);
                    await observer.OnStop(cancellationToken);
                    ClientLifecycleEvents.EmitObserverStopped(name, stage, stageName, clientAddress, stopwatch.Elapsed, observer);
                }
                catch (Exception exception)
                {
                    ClientLifecycleEvents.EmitObserverFailed(name, stage, stageName, clientAddress, exception, stopwatch.Elapsed, observer);
                    throw;
                }
            }
        }
    }
}
