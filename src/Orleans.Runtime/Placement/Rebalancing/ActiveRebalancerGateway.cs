using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using Orleans.Placement.Rebalancing;
using Orleans.Metadata;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Placement.Rebalancing;

#nullable enable

internal sealed class ActiveRebalancerGateway : IActiveRebalancerGateway, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver, IDisposable
{
    private const int ChannelCapacity = 100_000;
    private Task? _channelTask;

    private readonly ILogger _logger;
    private readonly IGrainFactory _grainFactory;
    private readonly ISiloStatusOracle _siloStatusOracle;
    private readonly GrainManifest _localManifest;
    private readonly PlacementStrategyResolver _strategyResolver;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<uint, bool> _migratableStatuses = new();
    private readonly GrainType _rebalancerGrain = GrainType.Create(IActiveRebalancerGrain.TypeName);
    private readonly Channel<CommEdge> _channel = Channel.CreateBounded<CommEdge>(new BoundedChannelOptions(ChannelCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false, // allow for maximum parallelism as messages will be dropped (we can afford to)
        FullMode = BoundedChannelFullMode.DropOldest // there is no need to analyze every single message 
    });

    public ActiveRebalancerGateway(
        ILoggerFactory loggerFactory,
        IGrainFactory grainFactory,
        ISiloStatusOracle siloStatusOracle,
        IClusterManifestProvider clusterManifestProvider,
        PlacementStrategyResolver strategyResolver)
    {
        _logger = loggerFactory.CreateLogger<ActiveRebalancerGateway>();
        _grainFactory = grainFactory;
        _siloStatusOracle = siloStatusOracle;
        _strategyResolver = strategyResolver;
        _localManifest = clusterManifestProvider.LocalGrainManifest;
    }

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(ActiveRebalancerGateway), ServiceLifecycleStage.ApplicationServices, this);

    void IDisposable.Dispose()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }

    public Task OnStart(CancellationToken cancellationToken)
    {
        _ = cancellationToken.Register(_cts.Cancel);

        _channelTask = Task.Factory.StartNew(
            () => ConsumeChannel(_cts.Token),
            _cts.Token,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default
        ).Unwrap();

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{Service} has started.", nameof(ActiveRebalancerGateway));
        }

        return Task.CompletedTask;
    }

    public async Task OnStop(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try
            {
                _cts.Cancel();
                if (_channelTask is null)
                {
                    return;
                }

                await _channelTask;
            }
            catch (Exception) { }
        }

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{Service} has stopped.", nameof(ActiveRebalancerGateway));
        }
    }

    private async Task ConsumeChannel(CancellationToken cancellationToken)
    {
        while (_siloStatusOracle.CurrentStatus != SiloStatus.Active)
        {
            // wait to make sure local silo is active, as we are going to place the rebalancer locally
        }

        var rebalancer = IActiveRebalancerGrain.GetReference(_grainFactory, _siloStatusOracle.SiloAddress);
        await rebalancer.AsReference<IActiveRebalancerExtension>().Activate();

        await foreach (var edge in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new TaskCanceledException("Channel consumtion was canceled");
            }

            await rebalancer.RecordEdge(edge);
        }
    }

    public void RecordMessage(Message message)
    {
        // Ignore system messages
        if (message.IsSystemMessage)
        {
            return;
        }

        // It must have a direction, and must not be a 'response' as it would skew analysis.
        if (message.HasDirection is false || message.Direction == Message.Directions.Response)
        {
            return;
        }

        // Sender and target need to be addressible grains to know where to move each.
        if (message.SendingGrain.IsDefault || message.TargetGrain.IsDefault)
        {
            return;
        }

        // Sender must exist, and must not be a client (we cant move grains to a client, only to a server).
        if (message.SendingSilo is null || message.SendingSilo.IsClient || message.SendingGrain.IsClient())
        {
            return;
        }

        // Target must exist, and must not be a client (we cant move grains to a client, only to a server).
        if (message.TargetSilo is null || message.TargetSilo.IsClient || message.TargetGrain.IsClient())
        { 
            return;
        }

        // Ignore rebalancer messages: either to another rebalancer, or when executing migration requests to activations.
        if (IsRebalancer(message.SendingGrain.Type) || IsRebalancer(message.TargetGrain.Type))
        {
            return;
        }

        // There are some edge cases when this can happen i.e. a grain invoking another one of its methods via AsReference<>, but we still exclude it
        // as wherever this grain would be located in the cluster, it would always be a local call (since it targets itself), this would add negative transfer cost
        // which would skew a potential relocation of this grain, while it shouldn't, because whenever this grain is located, it would still make local calls to itself.
        if (message.SendingGrain == message.TargetGrain)
        {
            return;
        }

        var isSenderMigratable = IsMigratable(message.SendingGrain.Type);
        var isTargetMigratable = IsMigratable(message.TargetGrain.Type);

        // If both are not migratable types we ignore this. But if one of them is not, than we allow passing, as we wish to move grains closer to them, as with any type of grain.
        if (!isSenderMigratable && !isTargetMigratable)
        {
            return;
        }

        CommEdge edge = new(
            new(message.SendingGrain, message.SendingSilo, isSenderMigratable),
            new(message.TargetGrain, message.TargetSilo, isTargetMigratable));

        _ = _channel.Writer.TryWrite(edge); // will always succeed as we drop messages if the channel is full

        bool IsRebalancer(GrainType grainType) => grainType.Equals(_rebalancerGrain);

        bool IsMigratable(GrainType grainType)
        {
            var hash = grainType.GetUniformHashCode();

            // _migratableStatuses holds statuses for each grain type if its migratable type or not, so we can make fast lookups.
            // since we don't anticipate a huge number of grain *types*, i think its just fine to have this in place as fast-check.
            if (!_migratableStatuses.TryGetValue(hash, out var isMigratable))
            {
                isMigratable = !(grainType.IsSystemTarget() || grainType.IsGrainService() || IsStatelessWorker(grainType) || IsImmovable(grainType));
                _migratableStatuses.TryAdd(hash, isMigratable);
            }

            return isMigratable;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool IsStatelessWorker(GrainType grainType) =>
                _strategyResolver.GetPlacementStrategy(grainType).GetType() == typeof(StatelessWorkerPlacement);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool IsImmovable(GrainType grainType) =>
                _localManifest.Grains.TryGetValue(grainType, out var props) &&
                props.Properties.TryGetValue(WellKnownGrainTypeProperties.Immovable, out var value) && bool.Parse(value);
        }
    }
}