#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Transport;
using Orleans.Runtime;
using Orleans.Runtime.Internal;
using Orleans.Serialization.Buffers;

namespace Orleans.TestingHost.InMemoryTransport;

internal class InMemoryMessageTransport : MessageTransportBase
{
    private const int MinReadSize = 256;
    private readonly Queue<ReadRequest> _readRequests = new();
    private readonly SingleWaiterAutoResetEvent _readSignal = new() { RunContinuationsAsynchronously = false };
    private readonly SingleWaiterAutoResetEvent _writeSignal = new() { RunContinuationsAsynchronously = true };
    private readonly Action _fireReadSignal;
    private readonly Action _fireWriteSignal;
    private readonly PipeReader _pipeReader;
    private readonly PipeWriter _pipeWriter;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _connectionClosingCts = new();
    private readonly CancellationTokenSource _processingCompleted = new();
    private readonly object _shutdownLock = new();
    private readonly object _writesLock = new();
    private readonly object _readsLock = new();
    private Queue<WriteRequest> _writeRequests = new();
    private bool _readsCompleted;
    private bool _writesCompleted;
    private Task? _processingTask;
    private volatile Exception? _shutdownReason;

    public InMemoryMessageTransport(IDuplexPipe pipe, ILogger logger)
    {
        _pipeReader = pipe.Input;
        _pipeWriter = pipe.Output;
        _logger = logger;

        _fireReadSignal = _readSignal.Signal;
        _fireWriteSignal = _writeSignal.Signal;
    }

    public override CancellationToken Closed => _processingCompleted.Token;

    public void Start()
    {
        using var _ = new ExecutionContextSuppressor();
        _processingTask = ProcessConnectionAsync();
    }

    private async Task ProcessConnectionAsync()
    {
        // Return immediately to the synchronous caller.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        try
        {
            // Spawn send and receive logic
            (var receiveTask, var sendTask) = StartProcessing();

            (Task ReceiveTask, Task SendTask) StartProcessing()
            {
                using (new ExecutionContextSuppressor())
                {
                    var receiveTask = ProcessReads();
                    var sendTask = ProcessWrites();
                    return (receiveTask, sendTask);
                }
            }

            // Wait for both to complete
            try
            {
                await receiveTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Unexpected exception in {nameof(InMemoryMessageTransport)}.{nameof(ProcessReads)}.");
            }

            try
            {
                await sendTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Unexpected exception in {nameof(InMemoryMessageTransport)}.{nameof(ProcessWrites)}.");
            }
        }
        catch (Exception ex)
        {
            _shutdownReason ??= ex;
            _logger.LogError(0, ex, $"Unexpected exception in {nameof(InMemoryMessageTransport)}.{nameof(ProcessConnectionAsync)}.");
        }
        finally
        {
            _processingCompleted.Cancel();
            await CloseCoreAsync();
        }
    }

    public override bool EnqueueRead(ReadRequest request)
    {
        if (_connectionClosingCts.IsCancellationRequested)
        {
            return false;
        }

        lock (_readsLock)
        {
            if (_readsCompleted)
            {
                return false;
            }

            _readRequests.Enqueue(request);
        }

        _readSignal.Signal();
        return true;
    }

    public override bool EnqueueWrite(WriteRequest request)
    {
        if (_connectionClosingCts.IsCancellationRequested)
        {
            return false;
        }

        lock (_writesLock)
        {
            if (_writesCompleted)
            {
                return false;
            }

            _writeRequests.Enqueue(request);
        }

        _writeSignal.Signal();
        return true;
    }

    public override async ValueTask CloseAsync(Exception? closeReason = null, CancellationToken cancellationToken = default)
    {
        if (_processingCompleted.IsCancellationRequested)
        {
            return;
        }

        _shutdownReason ??= closeReason;
        await CloseCoreAsync();

        if (_processingTask is null)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _processingCompleted.Token.Register(OnClosed, completion, useSynchronizationContext: false);

        // Wait for completion or cancellation
        try
        {
            await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // If cancellation was requested, force close
            _connectionClosingCts.Cancel();
        }

        static void OnClosed(object? state)
        {
            if (state is not TaskCompletionSource completion) throw new ArgumentException(nameof(state));
            completion.TrySetResult();
        }
    }

    private async Task CloseCoreAsync()
    {
        await _pipeReader.CompleteAsync();
        await _pipeWriter.CompleteAsync();

        _connectionClosingCts.Cancel();
        _readSignal.Signal();
        _writeSignal.Signal();
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync(null);
        await base.DisposeAsync();
    }

    private async Task ProcessReads()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        bool isGracefulTermination = false;
        Exception? error = null;
        ReadRequest? request = null;
        ReadOnlySequence<byte> readBuffer = default;
        using ArcBufferWriter bufferWriter = new();
        bool hasRead = false;
        try
        {
            // Loop until termination.
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                // Handle each request.
                while (TryDequeue(out request))
                {
                    // Process the request to completion.
                    while (true)
                    {
                        if (hasRead)
                        {
                            _pipeReader.AdvanceTo(readBuffer.Start, readBuffer.End);
                        }

                        var readResult = await _pipeReader.ReadAsync(_connectionClosingCts.Token);
                        hasRead = true;

                        if (readResult.IsCanceled || readResult.IsCompleted)
                        {
                            goto gracefulTermination;
                        }

                        bufferWriter.Write(readResult.Buffer);
                        readBuffer = readResult.Buffer.Slice(readResult.Buffer.Length);

                        if (request.OnRead(new ArcBufferReader(bufferWriter)))
                        {
                            break;
                        }
                    }
                }

                await _readSignal.WaitAsync().ConfigureAwait(false);
            }

gracefulTermination:
            isGracefulTermination = true;
        }
        catch (Exception exception)
        {
            error = exception;
            isGracefulTermination = false;
        }
        finally
        {
            if (isGracefulTermination)
            {
                request?.OnCanceled();
            }
            else
            {
                Debug.Assert(error is not null);
                request?.OnError(error);
            }

            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
            _writeSignal.Signal();

            lock (_readsLock)
            {
                _readsCompleted = true;
            }

            while (TryDequeue(out request))
            {
                if (isGracefulTermination)
                {
                    request.OnCanceled();
                }
                else
                {
                    Debug.Assert(error is not null);
                    request.OnError(error);
                }
            }
        }

        bool TryDequeue([NotNullWhen(true)] out ReadRequest? request)
        {
            lock (_readsLock)
            {
                return _readRequests.TryDequeue(out request);
            }
        }
    }

    private async Task ProcessWrites()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        const int SoftBatchMax = 32;
        Exception? error = null;
        Queue<WriteRequest> requests = new();
        List<ArraySegment<byte>> buffers = new(capacity: SoftBatchMax);
        List<WriteRequest> processingRequests = new(capacity: SoftBatchMax);

        try
        {
            // Loop until termination.
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                if (requests.Count == 0)
                {
                    // Check for pending messages before waiting.
                    RefreshRequestQueue(ref requests);

                    if (requests.Count == 0)
                    {
                        await _writeSignal.WaitAsync().ConfigureAwait(false);
                        continue;
                    }
                }

                buffers.Clear();
                processingRequests.Clear();

                while (processingRequests.Count < SoftBatchMax && requests.TryDequeue(out var request))
                {
                    processingRequests.Add(request);
                    using var slice = request.Buffers.ConsumeSlice(request.Buffers.Length);
                    foreach (var buffer in slice.MemorySegments)
                    {
                        var flushResult = await _pipeWriter.WriteAsync(buffer, _connectionClosingCts.Token);
                        if (flushResult.IsCanceled)
                        {
                            error = new OperationCanceledException();
                            break;
                        }

                        if (flushResult.IsCompleted)
                        {
                            break;
                        }
                    }
                }

                if (error is not null)
                {
                    // Bubble the error up
                    break;
                }

                // Signal that the requests are completed
                foreach (var request in processingRequests)
                {
                    request.SetResult();
                }
            }

            processingRequests.Clear();
        }
        catch (Exception ex)
        {
            // This is unexpected.
            error = ex;
        }
        finally
        {
            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();

            var requestError = _shutdownReason ?? new ConnectionClosedException();
            foreach (var request in processingRequests)
            {
                request.SetException(requestError);
            }

            _readSignal.Signal();

            lock (_writesLock)
            {
                _writesCompleted = true;
            }

            // Drain requests.
            while (requests.TryDequeue(out var request) || _writeRequests.TryDequeue(out request))
            {
                request.SetException(requestError);
            }
        }

        void RefreshRequestQueue(ref Queue<WriteRequest> queue)
        {
            lock (_writesLock)
            {
                queue = Interlocked.Exchange(ref _writeRequests, queue);
            }
        }
    }

    public override string ToString() => $"InMemoryTransport()";
}
