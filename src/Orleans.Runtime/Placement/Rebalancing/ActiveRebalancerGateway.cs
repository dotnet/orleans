using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using Orleans.Placement.Rebalancing;

namespace Orleans.Runtime.Placement.Rebalancing;

#nullable enable

internal sealed class ActiveRebalancerGateway : IActiveRebalancerGateway, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver, IDisposable
{
    private const int ChannelCapacity = 100_000;
    private Task? _channelTask;

    private readonly ILogger _logger;
    private readonly IGrainFactory _grainFactory;
    private readonly ISiloStatusOracle _siloStatusOracle;
    private readonly IRebalancingMessageFilter _messageFilter;
    private readonly CancellationTokenSource _cts = new();
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
        IRebalancingMessageFilter messageFilter)
    {
        _logger = loggerFactory.CreateLogger<ActiveRebalancerGateway>();
        _grainFactory = grainFactory;
        _siloStatusOracle = siloStatusOracle;
        _messageFilter = messageFilter;
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
        if (!_messageFilter.IsAcceptable(message, out var isSenderMigratable, out var isTargetMigratable))
        {
            return;
        }

        CommEdge edge = new(
            new(message.SendingGrain, message.SendingSilo, isSenderMigratable),
            new(message.TargetGrain, message.TargetSilo, isTargetMigratable));

        _ = _channel.Writer.TryWrite(edge); // will always succeed as we drop messages if the channel is full
    }
}