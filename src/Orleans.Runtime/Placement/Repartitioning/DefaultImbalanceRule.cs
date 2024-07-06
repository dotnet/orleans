using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;
using Orleans.Placement.Repartitioning;

namespace Orleans.Runtime.Placement.Repartitioning;

/// <summary>
/// Tolerance rule which is aware of the cluster size.
/// </summary>
internal sealed class DefaultImbalanceRule(ISiloStatusOracle siloStatusOracle) : IImbalanceToleranceRule,
    ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver, ISiloStatusListener
{
    private const double Baseline = 10.1d;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<SiloAddress, SiloStatus> _silos = new();
    private readonly ISiloStatusOracle _siloStatusOracle = siloStatusOracle;

    private uint _allowedImbalance = 0;

    public bool IsSatisfiedBy(uint imbalance) => imbalance <= _allowedImbalance;

    public void SiloStatusChangeNotification(SiloAddress silo, SiloStatus status)
    {
        _ = _silos.AddOrUpdate(silo, static (_, arg) => arg, static (_, _, arg) => arg, status);
        lock (_lock)
        {
            var activeSilos = _silos.Count(s => s.Value == SiloStatus.Active);   
            var percentageOfBaseline = 100d / (1 + Math.Exp(0.07d * activeSilos - 4.8d)); // inverted sigmoid
            if (percentageOfBaseline < 10d)
            {
                percentageOfBaseline = 10d;
            }

            // silos: 2 => tolerance: ~ 1000
            // silos: 100 => tolerance: ~ 100
            _allowedImbalance = (uint)Math.Round(Baseline * percentageOfBaseline, 0);
        }
    }

    public void Participate(ISiloLifecycle lifecycle)
        => lifecycle.Subscribe(nameof(DefaultImbalanceRule), ServiceLifecycleStage.ApplicationServices, this);

    public Task OnStart(CancellationToken cancellationToken = default)
    {
        _siloStatusOracle.SubscribeToSiloStatusEvents(this);
        return Task.CompletedTask;
    }
    public Task OnStop(CancellationToken cancellationToken = default)
    {
        _siloStatusOracle.UnSubscribeFromSiloStatusEvents(this);
        return Task.CompletedTask;
    }
}