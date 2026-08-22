using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationService : IAsyncDisposable
{
    private readonly Orleans.AsyncSerialExecutor<object?> _executor = new();
    private readonly DisseminationProtocol _protocol;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DisseminationProtocol> _logger;
    private readonly object _lifecycleLock = new();
    private IDisposable? _optionsChangeRegistration;
    private CancellationTokenSource? _shutdownCts;
    private CancellationTokenSource? _antiEntropyCts;
    private Task? _antiEntropyTask;
    private Task? _protocolDisposeTask;

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
        _protocol = new DisseminationProtocol(transport, options, topics, timeProvider, logger);
    }

    public ValueTask<bool> Publish(
        string topicName,
        DisseminationValue value,
        IReadOnlyCollection<SiloAddress>? targetPeers,
        CancellationToken cancellationToken) =>
        new(Execute(async () => await _protocol.Publish(topicName, value, targetPeers, cancellationToken)));

    public Task ReceiveGossip(DisseminationGossipBatch batch, CancellationToken cancellationToken) =>
        Execute(async () => await _protocol.ReceiveGossip(batch, cancellationToken));

    public ValueTask<DisseminationAntiEntropyResponse> ReceiveAntiEntropy(
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken) =>
        new(Execute(async () => await _protocol.ReceiveAntiEntropy(request, cancellationToken)));

    internal IReadOnlyList<SiloAddress> GetUnconfirmedPeers(
        string topicName,
        DisseminationMembershipScope membershipScope,
        IReadOnlyCollection<SiloAddress>? candidates = null) =>
        _protocol.GetUnconfirmedPeers(topicName, membershipScope, candidates);

    internal Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_shutdownCts is not null || _protocolDisposeTask is not null)
            {
                return Task.CompletedTask;
            }

            _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _optionsChangeRegistration = _options.OnChange((updated, _) =>
            {
                if (updated.Enabled)
                {
                    StartAntiEntropyLoop();
                }
                else
                {
                    StopAntiEntropyLoop();
                }
            });
            if (_options.CurrentValue.Enabled)
            {
                StartAntiEntropyLoopUnsafe();
            }
        }

        return Task.CompletedTask;
    }

    internal bool IsAntiEntropyRunning =>
        _shutdownCts is { IsCancellationRequested: false }
        && _antiEntropyTask is { IsCompleted: false };

    internal bool HasOutstandingAntiEntropyTask => _antiEntropyTask is not null;

    internal bool IsProtocolDisposed => _protocol.IsDisposed;

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? antiEntropyTask;
        CancellationTokenSource? shutdownCts;
        CancellationTokenSource? antiEntropyCts;
        IDisposable? optionsChangeRegistration;
        lock (_lifecycleLock)
        {
            antiEntropyTask = _antiEntropyTask;
            antiEntropyCts = _antiEntropyCts;
            shutdownCts = _shutdownCts;
            _shutdownCts = null;
            optionsChangeRegistration = _optionsChangeRegistration;
            _optionsChangeRegistration = null;
            antiEntropyCts?.Cancel();
            shutdownCts?.Cancel();
        }

        Exception? cancellationException = null;
        var deferAntiEntropyCleanup = false;
        try
        {
            optionsChangeRegistration?.Dispose();
            if (antiEntropyTask is not null)
            {
                try
                {
                    await antiEntropyTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationException = exception;
                    deferAntiEntropyCleanup = !antiEntropyTask.IsCompleted;
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

            try
            {
                await Execute(async () => await _protocol.StopAsync(cancellationToken));
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationException ??= exception;
            }
        }
        finally
        {
            if (deferAntiEntropyCleanup && antiEntropyTask is not null)
            {
                ObserveAntiEntropyShutdown(antiEntropyTask, antiEntropyCts);
            }
            else
            {
                CompleteAntiEntropyShutdown(antiEntropyTask, antiEntropyCts);
            }

            shutdownCts?.Dispose();
        }

        var protocolDisposeTask = GetProtocolDisposeTask();
        if (cancellationException is null)
        {
            await protocolDisposeTask;
        }

        if (cancellationException is not null)
        {
            throw cancellationException;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? protocolDisposeTask;
        Task? antiEntropyTask;
        lock (_lifecycleLock)
        {
            protocolDisposeTask = _protocolDisposeTask;
            antiEntropyTask = _antiEntropyTask;
        }

        if (protocolDisposeTask is null)
        {
            await StopAsync(CancellationToken.None);
            return;
        }

        if (antiEntropyTask is not null)
        {
            try
            {
                await antiEntropyTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                LogDebugAntiEntropyLoopFailed(_logger, exception);
            }
        }

        await protocolDisposeTask;
    }

    internal async Task RunAntiEntropy(CancellationToken cancellationToken)
    {
        var state = await Execute(() => Task.FromResult(_protocol.CreateAntiEntropyState()));
        var responses = await _protocol.ExchangeAntiEntropy(state, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await Execute(async () => await _protocol.ApplyAntiEntropyResponses(responses, cancellationToken));
    }

    private void ObserveAntiEntropyShutdown(Task task, CancellationTokenSource? cancellationTokenSource) =>
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var (service, source) = ((DisseminationService, CancellationTokenSource?))state!;
                service.CompleteAntiEntropyShutdown(completed, source);
            },
            (this, cancellationTokenSource),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void CompleteAntiEntropyShutdown(Task? task, CancellationTokenSource? cancellationTokenSource)
    {
        if (task is not null)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                LogDebugAntiEntropyLoopFailed(_logger, exception);
            }
        }

        lock (_lifecycleLock)
        {
            if (ReferenceEquals(_antiEntropyTask, task))
            {
                _antiEntropyTask = null;
            }

            if (ReferenceEquals(_antiEntropyCts, cancellationTokenSource))
            {
                _antiEntropyCts = null;
            }
        }

        cancellationTokenSource?.Dispose();
    }

    private Task GetProtocolDisposeTask()
    {
        lock (_lifecycleLock)
        {
            return _protocolDisposeTask ??= _protocol.DisposeAsync().AsTask();
        }
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

    private void StartAntiEntropyLoop()
    {
        lock (_lifecycleLock)
        {
            StartAntiEntropyLoopUnsafe();
        }
    }

    private void StartAntiEntropyLoopUnsafe()
    {
        if (!_options.CurrentValue.Enabled
            || _shutdownCts is not { IsCancellationRequested: false } shutdownCts)
        {
            return;
        }

        if (_antiEntropyTask is { IsCompleted: false } runningTask)
        {
            if (_antiEntropyCts?.IsCancellationRequested == true)
            {
                _ = runningTask.ContinueWith(
                    static (_, state) => ((DisseminationService)state!).StartAntiEntropyLoop(),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return;
        }

        _antiEntropyCts?.Dispose();
        _antiEntropyCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
        var cancellationToken = _antiEntropyCts.Token;
        _antiEntropyTask = Task.Run(() => RunAntiEntropyLoop(cancellationToken), CancellationToken.None);
    }

    private void StopAntiEntropyLoop()
    {
        lock (_lifecycleLock)
        {
            _antiEntropyCts?.Cancel();
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
