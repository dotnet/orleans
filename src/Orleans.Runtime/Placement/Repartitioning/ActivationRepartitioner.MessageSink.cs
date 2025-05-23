#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime.Placement.Repartitioning;

internal sealed partial class ActivationRepartitioner : IMessageStatisticsSink
{
    private readonly CancellationTokenSource _shutdownCts = new();

    // This filter contains grain ids which will are anchored to the current silo.
    // Ids are inserted when a grain is found to have a negative transfer score.
    private readonly BlockedBloomFilter? _anchoredFilter;
    private Task? _processPendingEdgesTask;

    public void StartProcessingEdges()
    {
        using var _ = new ExecutionContextSuppressor();
        _processPendingEdgesTask = ProcessPendingEdges(_shutdownCts.Token);

        LogTraceServiceStarted(_logger, nameof(ActivationRepartitioner));
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

        LogTraceServiceStopped(_logger, nameof(ActivationRepartitioner));
    }

    private async Task ProcessPendingEdges(CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);

        var drainBuffer = new Message[128];
        var iteration = 0;
        const int MaxIterationsPerYield = 128;
        while (!cancellationToken.IsCancellationRequested)
        {
            var count = _pendingMessages.DrainTo(drainBuffer);
            if (count > 0)
            {
                foreach (var message in drainBuffer[..count])
                {
                    if (!_messageFilter.IsAcceptable(message, out var isSenderMigratable, out var isTargetMigratable))
                    {
                        continue;
                    }

                    EdgeVertex sourceVertex;
                    if (_anchoredFilter != null && _anchoredFilter.Contains(message.SendingGrain) && Silo.Equals(message.SendingSilo))
                    {
                        sourceVertex = new(GrainId, Silo, isMigratable: false);
                    }
                    else
                    {
                        sourceVertex = new(message.SendingGrain, message.SendingSilo, isSenderMigratable);
                    }

                    EdgeVertex destinationVertex;
                    if (_anchoredFilter != null && _anchoredFilter.Contains(message.TargetGrain) && Silo.Equals(message.TargetSilo))
                    {
                        destinationVertex = new(GrainId, Silo, isMigratable: false);
                    }
                    else
                    {
                        destinationVertex = new(message.TargetGrain, message.TargetSilo, isTargetMigratable);
                    }

                    if (!sourceVertex.IsMigratable && !destinationVertex.IsMigratable)
                    {
                        // Ignore edges between two non-migratable grains.
                        continue;
                    }

                    Edge edge = new(sourceVertex, destinationVertex);
                    _edgeWeights.Add(edge);
                }

                if (++iteration >= MaxIterationsPerYield)
                {
                    iteration = 0;
                    await Task.Delay(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond), CancellationToken.None);
                }
            }
            else
            {
                iteration = 0;
                await _pendingMessageEvent.WaitAsync();
            }
        }
    }

    public Action<Message>? GetMessageObserver() => RecordMessage;

    private void RecordMessage(Message message)
    {
        if (!_enableMessageSampling || message.IsSystemMessage)
        {
            return;
        }

        // It must have a direction, and must not be a 'response' as it would skew analysis.
        if (message.Direction is Message.Directions.None or Message.Directions.Response)
        {
            return;
        }

        // Sender and target need to be fully addressable to know where to move to or towards.
        if (!message.IsSenderFullyAddressed || !message.IsTargetFullyAddressed)
        {
            return;
        }

        if (_pendingMessages.TryAdd(message) == Utilities.BufferStatus.Success)
        {
            _pendingMessageEvent.Signal();
        }
    }

    async ValueTask IActivationRepartitionerSystemTarget.FlushBuffers()
    {
        while (_pendingMessages.Count > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30));
        }
    }

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{Service} has started."
    )]
    private static partial void LogTraceServiceStarted(ILogger logger, string service);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{Service} has stopped."
    )]
    private static partial void LogTraceServiceStopped(ILogger logger, string service);
}