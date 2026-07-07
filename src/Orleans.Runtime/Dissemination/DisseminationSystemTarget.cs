using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationSystemTarget : SystemTarget, IDisseminationSystemTarget, IDisseminationService, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly DisseminationProtocol _protocol;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly ILogger<DisseminationProtocol> _logger;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _antiEntropyTask;

    public DisseminationSystemTarget(
        IDisseminationTransport transport,
        DisseminationMembership membership,
        IOptionsMonitor<DisseminationOptions> options,
        IEnumerable<IDisseminationTopic> topics,
        TimeProvider timeProvider,
        ILogger<DisseminationProtocol> logger,
        SystemTargetShared shared)
        : base(Constants.DisseminationSystemTargetType, shared)
    {
        _options = options;
        _logger = logger;
        _protocol = new DisseminationProtocol(transport, membership, options, topics, timeProvider, logger);
        _timer = new PeriodicTimer(_options.CurrentValue.Overlay.AntiEntropyInterval, timeProvider);
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    async ValueTask<bool> IDisseminationService.Publish(
        string topicName,
        DisseminationValue value,
        CancellationToken cancellationToken) =>
        await this.RunOrQueueTask(async () => await _protocol.Publish(topicName, value, cancellationToken));

    Task IDisseminationSystemTarget.PushGossip(DisseminationGossipBatch batch, CancellationToken cancellationToken) =>
        _protocol.ReceiveGossip(batch, cancellationToken);

    async Task<DisseminationAntiEntropyResponse> IDisseminationSystemTarget.ExchangeAntiEntropy(
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken) =>
    await _protocol.ReceiveAntiEntropy(request, cancellationToken);

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(DisseminationSystemTarget),
            ServiceLifecycleStage.RuntimeServices,
            StartAsync,
            StopAsync);
    }

    private Task StartAsync(CancellationToken cancellationToken)
    {
        if (_antiEntropyTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _antiEntropyTask = this.RunOrQueueTask(RunAntiEntropyLoop);
        return Task.CompletedTask;
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Dispose();
        await this.RunOrQueueTask(() => _protocol.FlushPendingGossip(cancellationToken));
        await _shutdownCts.CancelAsync();
        if (_antiEntropyTask is not null)
        {
            try
            {
                await _antiEntropyTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during silo shutdown.
            }
            catch (Exception exception)
            {
                LogDebugAntiEntropyLoopFailed(_logger, exception);
            }
        }

        _antiEntropyTask = null;
        _shutdownCts.Dispose();
    }

    private async Task RunAntiEntropyLoop()
    {
        var cancellationToken = _shutdownCts.Token;
        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken))
            {
                _timer.Period = _options.CurrentValue.Overlay.AntiEntropyInterval;
                try
                {
                    var state = _protocol.CreateAntiEntropyState();
                    var responses = await _protocol.ExchangeAntiEntropy(state, cancellationToken);
                    await _protocol.ApplyAntiEntropyResponses(responses, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    LogDebugAntiEntropyLoopFailed(_logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during silo shutdown.
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination anti-entropy loop iteration failed.")]
    private static partial void LogDebugAntiEntropyLoopFailed(ILogger logger, Exception exception);
}
