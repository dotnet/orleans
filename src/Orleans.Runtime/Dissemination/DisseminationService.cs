using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationService
{
    private readonly Orleans.AsyncSerialExecutor<object?> _executor = new();
    private readonly DisseminationProtocol _protocol;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DisseminationProtocol> _logger;
    private readonly Dictionary<string, IDisseminationTopic> _topics;
    private CancellationTokenSource? _shutdownCts;
    private Task? _antiEntropyTask;

    public DisseminationService(
        IDisseminationTransport transport,
        IOptionsMonitor<DisseminationOptions> options,
        IEnumerable<IDisseminationTopic> topics,
        TimeProvider timeProvider,
        ILogger<DisseminationProtocol> logger)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _topics = topics.ToDictionary(static topic => topic.Name, StringComparer.Ordinal);
        _protocol = new DisseminationProtocol(transport, options, _topics.Values, timeProvider, logger);
    }

    public ValueTask<bool> Publish(
        string topicName,
        DisseminationValue value,
        IReadOnlyCollection<SiloAddress>? targetPeers,
        CancellationToken cancellationToken) =>
        new(Execute(async () => await _protocol.Publish(topicName, value, targetPeers, cancellationToken)));

    public DisseminationCapabilityResponse GetCapabilities(DisseminationCapabilityRequest request)
    {
        if (!_options.CurrentValue.Enabled
            || !_topics.TryGetValue(request.Topic, out var topic)
            || !topic.IsEnabled
            || request.ProtocolVersion > topic.ProtocolVersion)
        {
            return new DisseminationCapabilityResponse
            {
                Topic = request.Topic,
                ProtocolVersion = request.ProtocolVersion,
                Supported = false,
            };
        }

        var payloadKinds = topic.PayloadKinds.Intersect(request.PayloadKinds, StringComparer.Ordinal).ToArray();
        return new DisseminationCapabilityResponse
        {
            Topic = request.Topic,
            ProtocolVersion = topic.ProtocolVersion,
            Supported = payloadKinds.Length > 0,
            PayloadKinds = payloadKinds,
        };
    }

    public Task ReceiveGossip(DisseminationGossipBatch batch, CancellationToken cancellationToken) =>
        Execute(async () => await _protocol.ReceiveGossip(batch, cancellationToken));

    public ValueTask<DisseminationAntiEntropyResponse> ReceiveAntiEntropy(
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken) =>
        new(Execute(async () => await _protocol.ReceiveAntiEntropy(request, cancellationToken)));

    internal Task StartAsync(CancellationToken cancellationToken)
    {
        if (_antiEntropyTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _antiEntropyTask = Task.Run(() => RunAntiEntropyLoop(_shutdownCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        await Execute(async () => await _protocol.FlushPendingGossip(cancellationToken));
        _shutdownCts?.Cancel();
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

        _shutdownCts?.Dispose();
        _shutdownCts = null;
        _antiEntropyTask = null;
    }

    internal async Task RunAntiEntropy(CancellationToken cancellationToken)
    {
        var state = await Execute(() => Task.FromResult(_protocol.CreateAntiEntropyState()));
        var responses = await _protocol.ExchangeAntiEntropy(state, cancellationToken);
        await Execute(async () => await _protocol.ApplyAntiEntropyResponses(responses, cancellationToken));
    }

    private async Task RunAntiEntropyLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var interval = _options.CurrentValue.Overlay.AntiEntropyInterval;
                await Task.Delay(interval, _timeProvider, cancellationToken);
                await RunAntiEntropy(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during silo shutdown.
            }
            catch (Exception exception)
            {
                LogDebugAntiEntropyLoopFailed(_logger, exception);
            }
        }
    }

    private async Task Execute(Func<Task> action)
    {
        await _executor.AddNext(async () =>
        {
            await action();
            return null;
        });
    }

    private async Task<T> Execute<T>(Func<Task<T>> action) =>
        (T)(await _executor.AddNext(async () => await action()))!;

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination anti-entropy loop iteration failed.")]
    private static partial void LogDebugAntiEntropyLoopFailed(ILogger logger, Exception exception);
}
