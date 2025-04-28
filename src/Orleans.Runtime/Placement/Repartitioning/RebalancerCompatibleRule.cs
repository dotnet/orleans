using System;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Orleans.Placement.Repartitioning;
using Orleans.Placement.Rebalancing;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Placement.Repartitioning;

#nullable enable

/// <summary>
/// Tolerance rule which is aware of the cluster size, and if rebalancer is enabled, it scales with the clusters imbalance.
/// </summary>
/// <remarks>https://www.ledjonbehluli.com/posts/orleans_repartioner_rebalancer_coexistence/</remarks>
internal class RebalancerCompatibleRule(IServiceProvider provider) :
    IImbalanceToleranceRule, ILifecycleParticipant<ISiloLifecycle>,
    ILifecycleObserver, ISiloStatusListener, IActivationRebalancerReportListener
{
    private readonly object _lock = new();
    private readonly Dictionary<SiloAddress, SiloStatus> _silos = [];

    private uint _pairwiseImbalance;
    private double _clusterImbalance; // If rebalancer is not registered this has no effect on computing the tolerance.

    private readonly ISiloStatusOracle _oracle = provider.GetRequiredService<ISiloStatusOracle>();
    private readonly IActivationRebalancer? _rebalancer = provider.GetService<IActivationRebalancer>();

    public bool IsSatisfiedBy(uint imbalance) => imbalance <= Volatile.Read(ref _pairwiseImbalance);

    public void SiloStatusChangeNotification(SiloAddress silo, SiloStatus status)
    {
        lock (_lock)
        {
            ref var statusRef = ref CollectionsMarshal.GetValueRefOrAddDefault(_silos, silo, out _);
            statusRef = status;
            UpdatePairwiseImbalance();
        }
    }

    public void Participate(ISiloLifecycle lifecycle)
        => lifecycle.Subscribe(nameof(RebalancerCompatibleRule),
               ServiceLifecycleStage.ApplicationServices, this);

    public void OnReport(RebalancingReport report)
    {
        lock (_lock)
        {
            _clusterImbalance = report.ClusterImbalance;
            UpdatePairwiseImbalance();
        }
    }

    private void UpdatePairwiseImbalance()
    {
        var activeSilos = _silos.Count(s => s.Value == SiloStatus.Active);
        var percentageOfBaseline = 100d / (1 + Math.Exp(0.07d * activeSilos - 4.8d));

        if (percentageOfBaseline < 10d) percentageOfBaseline = 10d;

        var pairwiseImbalance = (uint)Math.Round(10.1d * percentageOfBaseline, 0);
        var toleranceFactor = Math.Cos(Math.PI * _clusterImbalance / 2);  // This will always be 1 if rebalancer is not registered.

        _pairwiseImbalance = (uint)Math.Max(pairwiseImbalance * toleranceFactor, 0);
    }

    public Task OnStart(CancellationToken cancellationToken)
    {
        _oracle.SubscribeToSiloStatusEvents(this);
        _rebalancer?.SubscribeToReports(this);

        return Task.CompletedTask;
    }

    public Task OnStop(CancellationToken cancellationToken)
    {
        _oracle.UnSubscribeFromSiloStatusEvents(this);
        _rebalancer?.UnsubscribeFromReports(this);

        return Task.CompletedTask;
    }
}
