#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime.Placement.Rebalancing;

internal partial class ActivationRebalancer : IMessageStatisticsSink
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _processPendingEdgesTask;

    public void StartProcessingEdges()
    {
        using var _ = new ExecutionContextSuppressor();
        _processPendingEdgesTask = ProcessPendingEdges(_shutdownCts.Token);

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{Service} has started.", nameof(ActivationRebalancer));
        }
    }

    public async Task StopProcessingEdgesAsync(CancellationToken cancellationToken)
    {
        _shutdownCts.Cancel();
        if (_processPendingEdgesTask is null)
        {
            return;
        }

        _pendingMessageEvent.Signal();
        await _processPendingEdgesTask.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{Service} has stopped.", nameof(ActivationRebalancer));
        }
    }

    private async Task ProcessPendingEdges(CancellationToken cancellationToken)
    {
        const int MaxCyclesPerYield = 100;
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);

        var drainBuffer = new Message[256];
        var cyclesPerYield = 100;
        while (!cancellationToken.IsCancellationRequested)
        {
            var count = _pendingMessages.DrainTo(drainBuffer);
            if (count > 0)
            {
                foreach (var message in drainBuffer[..count])
                {
                    _messageFilter.IsAcceptable(message, out var isSenderMigratable, out var isTargetMigratable);

                    Edge edge = new(
                        new(message.SendingGrain, message.SendingSilo, isSenderMigratable),
                        new(message.TargetGrain, message.TargetSilo, isTargetMigratable));
                    _edgeWeights.Add(edge);
                }
            }
            else
            {
                await _pendingMessageEvent.WaitAsync();
            }

            if (++cyclesPerYield >= MaxCyclesPerYield)
            {
                cyclesPerYield = 0;
                await Task.Yield();
            }
        }
    }

    public void RecordMessage(Message message)
    {
        if (!_enableMessageSampling || !_messageFilter.IsAcceptable(message, out var isSenderMigratable, out var isTargetMigratable))
        {
            return;
        }

        if (_pendingMessages.TryAdd(message) == Utilities.BufferStatus.Success)
        {
            _pendingMessageEvent.Signal();
        }
    }
}