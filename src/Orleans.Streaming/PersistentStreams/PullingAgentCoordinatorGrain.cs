using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Runtime;

namespace Orleans.Streams;

[GrainInterfaceType("Orleans.Streams.IStreamPullingAgentCoordinator")]
internal interface IPullingAgentCoordinatorGrain : IGrain
{
    [AlwaysInterleave]
    [Alias("233F3872")]
    Task EnsureRunning(CancellationToken cancellationToken = default);
    [AlwaysInterleave]
    [Alias("38F87745")]
    Task NotifyHostChanged(CancellationToken cancellationToken = default);
    [Alias("2BBC562F")]
    Task<Dictionary<QueueId, StreamPullingAgentStatus>> GetAgents(CancellationToken cancellationToken = default);
}

[GrainType(GrainTypeName)]
[StreamPullingAgentPlacement]
[Immovable]
internal sealed class PullingAgentCoordinatorGrain(
    StreamPullingAgentRuntime runtime,
    StreamPullingAgentHostResolver hosts,
    IClusterMembershipService membership,
    [FromKeyedServices(TimeProviderNames.Grains)] TimeProvider clock,
    ILogger<PullingAgentCoordinatorGrain> logger) : Grain, IPullingAgentCoordinatorGrain
{
    internal const string GrainTypeName = "Orleans.Streams.PullingAgentCoordinator";
    internal static readonly GrainType GrainType = GrainType.Create(GrainTypeName);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<QueueId, Agent> _agents = new();
    private readonly Dictionary<SiloAddress, HashSet<QueueId>> _agentsBySilo = new();
    private HashSet<SiloAddress> _eligibleSilos = new();
    private StreamPullingAgentOptions _options = null!;
    private string _providerName = null!;
    private IGrainTimer? _timer;
    private Task _membershipTask = Task.CompletedTask;
    private long? _rebalanceSince;
    private bool _reconcileRequested;
    private bool _stopping;

    internal static GrainId GetGrainId(string providerName) => GrainId.Create(GrainType, providerName);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _providerName = GrainContext.GrainId.Key.ToString();
        var provider = runtime.GetProvider(_providerName);
        _options = provider.Options;
        foreach (var queue in provider.Queues)
        {
            _agents.Add(queue, new(GrainFactory.GetGrain<IPullingAgentGrain>(StreamPullingAgentId.Create(_providerName, queue))));
        }

        EnsureTimer();
        _membershipTask = ObserveMembership(_shutdown.Token);
        RequestReconciliation();
        return Task.CompletedTask;
    }

    public Task EnsureRunning(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_timer is null)
        {
            EnsureTimer();
            RequestReconciliation();
        }

        return Task.CompletedTask;
    }

    public Task NotifyHostChanged(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTimer();
        RequestReconciliation();
        return Task.CompletedTask;
    }

    public Task<Dictionary<QueueId, StreamPullingAgentStatus>> GetAgents(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_agents.Where(static entry => entry.Value.Status.HasValue)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value.Status!.Value));
    }

    private void EnsureTimer()
    {
        if (!_stopping)
        {
            _timer ??= this.RegisterGrainTimer(Reconcile, new()
            {
                DueTime = _options.GrainHostingProbePeriod,
                Period = _options.GrainHostingProbePeriod,
                KeepAlive = true,
            });
        }
    }

    private void RequestReconciliation()
    {
        _reconcileRequested = true;
        _timer?.Change(TimeSpan.Zero, _options.GrainHostingProbePeriod);
    }

    private async Task ObserveMembership(CancellationToken cancellationToken)
    {
        var activeSilos = GetActiveSilos(membership.CurrentSnapshot);
        try
        {
            await foreach (var snapshot in membership.MembershipUpdates.WithCancellation(cancellationToken))
            {
                var current = GetActiveSilos(snapshot);
                if (!activeSilos.SetEquals(current))
                {
                    activeSilos = current;
                    RequestReconciliation();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Membership observation failed for pulling-agent coordinator {ProviderName}.", _providerName);
            this.DeactivateOnIdle();
        }
    }

    private static HashSet<SiloAddress> GetActiveSilos(ClusterMembershipSnapshot snapshot)
        => snapshot.Members.Values.Where(static member => member.Status == SiloStatus.Active)
            .Select(static member => member.SiloAddress).ToHashSet();

    private async Task Reconcile(CancellationToken cancellationToken)
    {
        _reconcileRequested = false;
        try
        {
            var eligible = (await hosts.GetEligibleSilos(
                _providerName, null, StreamPullingAgentId.GrainType,
                PullingAgentGrain.InterfaceType, cancellationToken)).ToHashSet();
            if (!_eligibleSilos.SetEquals(eligible))
            {
                _eligibleSilos = eligible;
                _rebalanceSince = clock.GetTimestamp();
            }

            if (eligible.Count == 0)
            {
                _timer?.Dispose();
                _timer = null;
                _agentsBySilo.Clear();
                foreach (var agent in _agents.Values)
                {
                    agent.Status = null;
                }

                _rebalanceSince = null;
                return;
            }

            var projectedCounts = eligible.ToDictionary(static silo => silo, static _ => 0);
            foreach (var agent in _agents.Values)
            {
                if (agent.Status is { IsRunning: true } status && projectedCounts.ContainsKey(status.Address.SiloAddress!))
                {
                    projectedCounts[status.Address.SiloAddress!]++;
                }
            }

            var probes = new List<Task<bool>>(_agents.Count);
            foreach (var (queue, agent) in _agents)
            {
                var hint = agent.Status is { IsRunning: true } previous && eligible.Contains(previous.Address.SiloAddress!)
                    ? previous.Address.SiloAddress!
                    : SelectLeastLoaded(projectedCounts);
                if (agent.Status is not { IsRunning: true } known || !eligible.Contains(known.Address.SiloAddress!))
                {
                    projectedCounts[hint]++;
                }

                probes.Add(Probe(queue, agent, hint, eligible, projectedCounts, cancellationToken));
            }

            var results = await Task.WhenAll(probes);
            RebuildDistribution();
            if (results.Any(static success => !success))
            {
                _rebalanceSince = null;
                return;
            }

            if (_reconcileRequested)
            {
                return;
            }

            await Rebalance(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _rebalanceSince = null;
            logger.LogError(exception, "Failed to reconcile pulling agents for stream provider {ProviderName}.", _providerName);
        }
    }

    private async Task<bool> Probe(
        QueueId queue,
        Agent agent,
        SiloAddress hint,
        HashSet<SiloAddress> eligible,
        Dictionary<SiloAddress, int> projectedCounts,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await StreamPullingAgentPlacement.WithHint(hint, () => agent.Grain.Probe(cancellationToken))
                .WaitAsync(cancellationToken);
            agent.Status = status;
            if (status.IsRunning && eligible.Contains(status.Address.SiloAddress!))
            {
                return true;
            }

            var destination = SelectLeastLoaded(projectedCounts);
            projectedCounts[destination]++;
            await agent.Grain.Rebalance(status.Address, destination, cancellationToken).WaitAsync(cancellationToken);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to probe pulling agent for provider {ProviderName}, queue {QueueId}.", _providerName, queue);
            return false;
        }
    }

    private void RebuildDistribution()
    {
        _agentsBySilo.Clear();
        foreach (var silo in _eligibleSilos)
        {
            _agentsBySilo[silo] = new();
        }

        foreach (var (queue, agent) in _agents)
        {
            if (agent.Status is { IsRunning: true } status && _agentsBySilo.TryGetValue(status.Address.SiloAddress!, out var queues))
            {
                queues.Add(queue);
            }
        }
    }

    private async Task Rebalance(CancellationToken cancellationToken)
    {
        var counts = _agentsBySilo.ToDictionary(static entry => entry.Key, static entry => entry.Value.Count);
        if (counts.Values.Max() - counts.Values.Min() <= 1)
        {
            _rebalanceSince = null;
            return;
        }

        _rebalanceSince ??= clock.GetTimestamp();
        if (clock.GetElapsedTime(_rebalanceSince.Value) < _options.GrainHostingRebalanceDelay)
        {
            return;
        }

        var available = _agentsBySilo.ToDictionary(static entry => entry.Key, static entry => new Queue<QueueId>(entry.Value.Order()));
        var requests = new List<Task<bool>>();
        while (true)
        {
            var source = counts.OrderByDescending(static entry => entry.Value).ThenBy(static entry => entry.Key).First();
            var destination = SelectLeastLoaded(counts);
            if (source.Value - counts[destination] <= 1)
            {
                break;
            }

            var queue = available[source.Key].Dequeue();
            var agent = _agents[queue];
            requests.Add(agent.Grain.Rebalance(agent.Status!.Value.Address, destination, cancellationToken));
            counts[source.Key]--;
            counts[destination]++;
            logger.LogInformation(
                "Requesting rebalance of pulling agent {GrainId} from {Source} to {Destination} for provider {ProviderName}.",
                agent.Status.Value.Address.GrainId, source.Key, destination, _providerName);
        }

        _rebalanceSince = clock.GetTimestamp();
        await Task.WhenAll(requests).WaitAsync(cancellationToken);
    }

    private static SiloAddress SelectLeastLoaded(Dictionary<SiloAddress, int> counts)
        => counts.OrderBy(static entry => entry.Value).ThenBy(static entry => entry.Key).First().Key;

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _stopping = true;
        _timer?.Dispose();
        _timer = null;
        _shutdown.Cancel();
        await _membershipTask;
        _shutdown.Dispose();
    }

    private sealed class Agent(IPullingAgentGrain grain)
    {
        internal IPullingAgentGrain Grain { get; } = grain;
        internal StreamPullingAgentStatus? Status { get; set; }
    }
}
