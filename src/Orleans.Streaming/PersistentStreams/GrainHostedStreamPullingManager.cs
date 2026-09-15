using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using StreamingEvents = Orleans.Streaming.Diagnostics.StreamingEvents;
using RunState = Orleans.Configuration.StreamLifecycleOptions.RunState;

namespace Orleans.Streams;

internal sealed class GrainHostedStreamPullingManager : SystemTarget, IPersistentStreamPullingManager
{
    private readonly string _providerName;
    private readonly StreamPullingAgentRuntime.Provider _provider;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly IPullingAgentCoordinatorGrain _coordinator;
    private readonly ILogger _logger;
    private readonly AsyncSerialExecutor _executor = new();
    private IGrainTimer? _heartbeat;
    private bool _shuttingDown;

    internal GrainHostedStreamPullingManager(
        SystemTargetGrainId id,
        string providerName,
        StreamPullingAgentRuntime.Provider provider,
        StreamInstruments streamInstruments,
        SystemTargetShared shared) : base(id, shared)
    {
        _providerName = providerName;
        _provider = provider;
        _grainFactory = shared.RuntimeClient.InternalGrainFactory;
        _coordinator = _grainFactory.GetGrain<IPullingAgentCoordinatorGrain>(PullingAgentCoordinatorGrain.GetGrainId(providerName));
        _logger = shared.LoggerFactory.CreateLogger<GrainHostedStreamPullingManager>();
        streamInstruments.RegisterPersistentStreamPullingAgentsObserve(() => new Measurement<int>(
            _provider.RunningAgentCount, new KeyValuePair<string, object?>("name", providerName)));
        streamInstruments.RegisterPersistentStreamPubSubCacheSizeObserve(ObservePubSubCacheSizes);
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public Task Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _provider.State = RunState.Initialized;
        return Task.CompletedTask;
    }

    public Task StartAgents(CancellationToken cancellationToken) => _executor.AddNext(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_shuttingDown)
        {
            throw new InvalidOperationException($"Stream provider '{_providerName}' is shutting down.");
        }

        _provider.State = RunState.AgentsStarted;
        _heartbeat ??= RegisterGrainTimer(KeepCoordinatorAlive, _provider.Options.GrainHostingProbePeriod, _provider.Options.GrainHostingProbePeriod);
        await StreamPullingAgentPlacement.WithHint(Silo, () => _coordinator.EnsureRunning(cancellationToken));
        await _coordinator.NotifyHostChanged(cancellationToken);
        EmitState();
    });

    public Task StopAgents(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var notifyCoordinator = _provider.State == RunState.AgentsStarted;
        CloseLocalAdmission();
        return _executor.AddNext(() => StopHostedAgents(notifyCoordinator));
    }

    public Task Stop(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _shuttingDown = true;
        CloseLocalAdmission();
        // Grain deactivation owns the final flush and producer-preserving migration during silo shutdown.
        return Task.CompletedTask;
    }

    private void CloseLocalAdmission()
    {
        _provider.State = RunState.AgentsStopped;
        _heartbeat?.Dispose();
        _heartbeat = null;
    }

    private async Task KeepCoordinatorAlive(CancellationToken cancellationToken)
    {
        if (_provider.State != RunState.AgentsStarted)
        {
            return;
        }

        try
        {
            await StreamPullingAgentPlacement.WithHint(Silo, () => _coordinator.EnsureRunning(cancellationToken));
            EmitState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to contact pulling-agent coordinator for provider {ProviderName}.", _providerName);
        }
    }

    private async Task StopHostedAgents(bool notifyCoordinator)
    {
        notifyCoordinator |= _provider.State == RunState.AgentsStarted;
        CloseLocalAdmission();
        var hostedQueues = _provider.Agents.Keys.ToArray();
        await Task.WhenAll(hostedQueues.Select(queueId => _grainFactory
            .GetGrain<IPullingAgentGrain>(StreamPullingAgentId.Create(_providerName, queueId))
            .Stop(Silo, CancellationToken.None)));
        EmitState();
        // Membership updates drive reconciliation during silo shutdown.
        if (notifyCoordinator && !_shuttingDown)
        {
            try
            {
                await _coordinator.NotifyHostChanged(CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to notify the coordinator after provider {ProviderName} stopped on {Silo}.", _providerName, Silo);
            }
        }
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
