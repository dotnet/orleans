using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using StreamingEvents = Orleans.Streaming.Diagnostics.StreamingEvents;
using RunState = Orleans.Configuration.StreamLifecycleOptions.RunState;

namespace Orleans.Streams;

internal sealed class GrainHostedStreamPullingManager : SystemTarget, IPersistentStreamPullingManager, IStreamQueueBalanceListener
{
    private static readonly TimeSpan ReconciliationPeriod = TimeSpan.FromSeconds(30);
    private readonly string _providerName;
    private readonly StreamPullingAgentRuntime.Provider _provider;
    private readonly IStreamQueueBalancer _balancer;
    private readonly IStreamQueueMapper _mapper;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly ILogger _logger;
    private readonly AsyncSerialExecutor _executor = new();
    private IGrainTimer? _reconciliationTimer;
    private bool _shuttingDown;

    internal GrainHostedStreamPullingManager(
        SystemTargetGrainId id,
        string providerName,
        StreamPullingAgentRuntime.Provider provider,
        IStreamQueueBalancer balancer,
        IStreamQueueMapper mapper,
        StreamInstruments streamInstruments,
        SystemTargetShared shared) : base(id, shared)
    {
        _providerName = providerName;
        _provider = provider;
        _balancer = balancer;
        _mapper = mapper;
        _grainFactory = shared.RuntimeClient.InternalGrainFactory;
        _logger = shared.LoggerFactory.CreateLogger<GrainHostedStreamPullingManager>();
        streamInstruments.RegisterPersistentStreamPullingAgentsObserve(() => new Measurement<int>(
            _provider.RunningAgentCount, new KeyValuePair<string, object?>("name", providerName)));
        streamInstruments.RegisterPersistentStreamPubSubCacheSizeObserve(ObservePubSubCacheSizes);
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public async Task Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _balancer.Initialize(_mapper);
        _provider.DesiredQueues = _balancer.GetMyQueues().ToImmutableHashSet();
        _provider.State = RunState.Initialized;
        _balancer.SubscribeToQueueDistributionChangeEvents(this);
    }

    public Task StartAgents(CancellationToken cancellationToken) => _executor.AddNext(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_shuttingDown)
        {
            throw new InvalidOperationException($"Stream provider '{_providerName}' is shutting down.");
        }

        _provider.State = RunState.AgentsStarted;
        _reconciliationTimer ??= RegisterGrainTimer(
            ReconcilePeriodically, ReconciliationPeriod, ReconciliationPeriod);
        await Reconcile(cancellationToken);
    });

    public Task StopAgents(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _provider.State = RunState.AgentsStopped;
        _reconciliationTimer?.Dispose();
        _reconciliationTimer = null;
        return _executor.AddNext(() => StopHostedAgents(cancellationToken));
    }

    public async Task Stop(CancellationToken cancellationToken)
    {
        _shuttingDown = true;
        _balancer.UnSubscribeFromQueueDistributionChangeEvents(this);
        await StopAgents(cancellationToken);
        await _balancer.Shutdown();
    }

    public Task QueueDistributionChangeNotification() => this.RunOrQueueTask(() => _executor.AddNext(async () =>
    {
        if (_provider.State == RunState.AgentsStarted)
        {
            await Reconcile(CancellationToken.None);
        }
    }));

    private Task ReconcilePeriodically(CancellationToken cancellationToken) => _executor.AddNext(async () =>
    {
        try
        {
            await Reconcile(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to reconcile pulling agents for stream provider {ProviderName} on {Silo}.", _providerName, Silo);
        }
    });

    private async Task Reconcile(CancellationToken cancellationToken)
    {
        if (_provider.State != RunState.AgentsStarted)
        {
            return;
        }

        var previous = _provider.DesiredQueues;
        var desired = _balancer.GetMyQueues().ToImmutableHashSet();
        _provider.DesiredQueues = desired;
        if (!previous.SetEquals(desired))
        {
            StreamingEvents.EmitQueueChange(_providerName, Silo, previous.ToArray(), desired.ToArray(), _balancer);
        }

        await Task.WhenAll(desired.Select(EnsureAgent));
        EmitState();

        async Task EnsureAgent(QueueId queueId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_provider.State != RunState.AgentsStarted || !_provider.DesiredQueues.Contains(queueId))
            {
                return;
            }

            var agent = _grainFactory.GetGrain<IGrainHostedStreamPullingAgent>(StreamPullingAgentId.Create(_providerName, queueId));
            var address = await agent.EnsureRunning(Silo, cancellationToken);
            if (address.SiloAddress != Silo)
            {
                _logger.LogDebug(
                    "Requested pulling agent {GrainId} move from {ActualHost} to {RequestedHost}.",
                    address.GrainId, address.SiloAddress, Silo);
            }
        }
    }

    private async Task StopHostedAgents(CancellationToken cancellationToken)
    {
        _provider.State = RunState.AgentsStopped;
        _reconciliationTimer?.Dispose();
        _reconciliationTimer = null;
        var hostedQueues = _provider.Agents.Keys.ToArray();
        await Task.WhenAll(hostedQueues.Select(queueId => _grainFactory
            .GetGrain<IGrainHostedStreamPullingAgent>(StreamPullingAgentId.Create(_providerName, queueId))
            .Stop(Silo, cancellationToken)));
        EmitState();
    }

    private void EmitState()
    {
        var runningQueues = _provider.GetRunningQueues();
        StreamingEvents.EmitPullingAgentManagerState(_providerName, Silo, runningQueues, runningQueues.Length);
    }

    private IEnumerable<Measurement<int>> ObservePubSubCacheSizes()
    {
        foreach (var entry in _provider.Agents)
        {
            yield return new Measurement<int>(
                entry.Value.PubSubCacheSize,
                new KeyValuePair<string, object?>("name", $"{_providerName}.{entry.Key}"));
        }
    }

    public async Task<object?> ExecuteCommand(PersistentStreamProviderCommand command, object? arg, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (command)
        {
            case PersistentStreamProviderCommand.StartAgents:
                await StartAgents(cancellationToken);
                return null;
            case PersistentStreamProviderCommand.StopAgents:
                await StopAgents(cancellationToken);
                return null;
            case PersistentStreamProviderCommand.GetAgentsState:
                return _provider.State;
            case PersistentStreamProviderCommand.GetNumberRunningAgents:
                return _provider.RunningAgentCount;
            default:
                throw new OrleansException($"PullingAgentManager does not support command {command}.");
        }
    }
}
