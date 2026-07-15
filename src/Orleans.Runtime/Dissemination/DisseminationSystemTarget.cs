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
        ILocalSiloDetails localSiloDetails,
        IInternalGrainFactory grainFactory,
        DisseminationMembership membership,
        IOptionsMonitor<DisseminationOptions> options,
        IEnumerable<IDisseminationNamespace> disseminationNamespaces,
        TimeProvider timeProvider,
        ILogger<DisseminationProtocol> logger,
        ILogger<DisseminationBroadcastQueue> broadcastQueueLogger,
        SystemTargetShared shared)
        : base(Constants.DisseminationSystemTargetType, shared)
    {
        _options = options;
        _logger = logger;
        _protocol = new DisseminationProtocol(localSiloDetails, grainFactory, membership, options, disseminationNamespaces, timeProvider, logger, broadcastQueueLogger);
        _timer = new PeriodicTimer(_options.CurrentValue.Overlay.AntiEntropyInterval, timeProvider);
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    async ValueTask<bool> IDisseminationService.Publish(
        IDisseminationNamespace disseminationNamespace,
        DisseminationKey key,
        long version,
        CancellationToken cancellationToken) =>
        await this.RunOrQueueTask(async () => await _protocol.Publish(
            disseminationNamespace,
            key,
            version,
            cancellationToken));

    Task<DisseminationBroadcastResponse> IDisseminationSystemTarget.PushBroadcast(
        DisseminationBroadcastBatch batch,
        CancellationToken cancellationToken) =>
        _protocol.ReceiveBroadcast(batch, cancellationToken);

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
        // Cancel the anti-entropy loop before disposing its timer so a pending wakeup cannot race timer disposal.
        await _shutdownCts.CancelAsync();
        _timer.Dispose();
        try
        {
            await this.RunOrQueueTask(() => _protocol.StopAsync(cancellationToken));
        }
        finally
        {
            // Always observe the loop and release the cancellation source, even if draining the protocol threw.
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

                _antiEntropyTask = null;
            }

            _shutdownCts.Dispose();
        }
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
                    await _protocol.RunAntiEntropyRound(cancellationToken);
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
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // The anti-entropy timer was disposed during shutdown after cancellation was requested.
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination anti-entropy loop iteration failed.")]
    private static partial void LogDebugAntiEntropyLoopFailed(ILogger logger, Exception exception);
}
