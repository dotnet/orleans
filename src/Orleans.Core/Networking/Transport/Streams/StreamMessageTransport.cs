#nullable enable

using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Internal;
using Orleans.Serialization.Buffers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Transport.Streams;

public abstract class StreamMessageTransport : MessageTransportBase
{
    private readonly ILogger _logger;
    private readonly SingleWaiterAutoResetEvent _writerSignal = new();
    private readonly SingleWaiterAutoResetEvent _readerSignal = new();
    private readonly Queue<WriteRequest> _pendingWrites = new();
    private readonly Queue<ReadRequest> _pendingReads = new();
    private readonly CancellationTokenSource _connectionClosingCts = new();
    private readonly CancellationTokenSource _connectionClosedCts = new();
    private readonly object _writesLock = new();
    private readonly object _readsLock = new();
    private readonly object _disposeLock = new();
    private Task? _runTask;
    private Exception? _shutdownReason;
    private bool _readsCompleted;
    private bool _writesCompleted;
    private volatile bool _streamDisposed;

    protected StreamMessageTransport(ILogger logger)
    {
        _logger = logger;
    }

    protected abstract Stream Stream { get; }

    public virtual void Start()
    {
        using var _ = new ExecutionContextSuppressor();
        _runTask = Task.Run(RunAsync);
    }

    public override CancellationToken Closed => _connectionClosedCts.Token;

    public override async ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default)
    {
        _shutdownReason ??= closeException;

        // Early exit if already closed
        if (_connectionClosedCts.IsCancellationRequested)
        {
            return;
        }

        // Signal loops to stop
        _connectionClosingCts.Cancel();
        _readerSignal.Signal();
        _writerSignal.Signal();

        // If no run task, just dispose the stream directly
        if (_runTask is null)
        {
            DisposeStream();
            return;
        }

        // Wait for processing to complete
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var closedRegistration = _connectionClosedCts.Token.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            useSynchronizationContext: false);
        using var cancelRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            completion,
            useSynchronizationContext: false);

        try
        {
            await completion.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Cancellation requested - caller wants to force close
            // Force dispose the stream to interrupt any pending I/O
            DisposeStream();

            // Signal completion so callers know we're done (even if not gracefully)
            _connectionClosedCts.Cancel();
        }
    }

    /// <summary>
    /// Disposes the underlying stream to force-close any pending I/O operations.
    /// </summary>
    private void DisposeStream()
    {
        if (_streamDisposed)
        {
            return;
        }

        lock (_disposeLock)
        {
            if (_streamDisposed)
            {
                return;
            }

            _streamDisposed = true;

            try
            {
                Stream.Dispose();
            }
            catch
            {
                // Ignore errors during disposal
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        // Ensure stream is disposed, even if CloseAsync wasn't called or timed out
        DisposeStream();

        // Signal that we're closing if not already done
        _connectionClosingCts.Cancel();
        _connectionClosedCts.Cancel();

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
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

            _pendingReads.Enqueue(request);
        }

        _readerSignal.Signal();
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

            _pendingWrites.Enqueue(request);
        }

        _writerSignal.Signal();
        return true;
    }

    private async Task RunAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        try
        {
            await RunAsyncCore();
        }
        finally
        {
            await CloseAsync(null);
        }
    }

    protected virtual async Task RunAsyncCore()
    {
        try
        {
            var readsTask = ProcessReads();
            var writesTask = ProcessWrites();
            await readsTask;
            await writesTask;
        }
        catch (Exception exception)
        {
            _shutdownReason ??= exception;
        }
        finally
        {
            _connectionClosedCts.Cancel();
        }
    }

    private async Task ProcessReads()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Exception? error = default;
        ReadRequest? operation = default;
        bool isGracefulTermination = false;
        using ArcBufferWriter bufferWriter = new();
        var reader = new ArcBufferReader(bufferWriter);
        try
        {
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                while (TryDequeue(out operation))
                {
                    while (true)
                    {
                        if (operation.OnRead(reader))
                        {
                            break;
                        }

                        var bytesRead = await Stream.ReadAsync(bufferWriter.GetMemory(), _connectionClosingCts.Token);
                        if (bytesRead == 0)
                        {
                            goto gracefulTermination;
                        }

                        bufferWriter.AdvanceWriter(bytesRead);
                    }
                }

                await _readerSignal.WaitAsync();
            }

gracefulTermination:
            isGracefulTermination = true;
        }
        catch (Exception exception)
        {
            // If we initiated shutdown (stream disposed or closing requested), treat as graceful
            if (_connectionClosingCts.IsCancellationRequested || _streamDisposed)
            {
                isGracefulTermination = true;
            }
            else
            {
                error ??= exception;
                isGracefulTermination = false;
            }
        }
        finally
        {
            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();

            lock (_readsLock)
            {
                _readsCompleted = true;
            }

            if (isGracefulTermination)
            {
                operation?.OnCanceled();
            }
            else
            {
                Debug.Assert(error is not null);
                operation?.OnError(error);
            }

            while (TryDequeue(out operation))
            {
                if (isGracefulTermination)
                {
                    operation.OnCanceled();
                }
                else
                {
                    Debug.Assert(error is not null);
                    operation.OnError(error);
                }
            }

            _writerSignal.Signal();

            // Only log unexpected errors (not when we intentionally disposed the stream)
            if (error is not null && !_streamDisposed)
            {
                _logger.LogError(0, error, $"Unexpected exception in {nameof(StreamMessageTransport)}.{nameof(ProcessReads)}.");
            }
        }

        bool TryDequeue([NotNullWhen(true)] out ReadRequest? operation)
        {
            lock (_readsLock)
            {
                return _pendingReads.TryDequeue(out operation);
            }
        }
    }

    private async Task ProcessWrites()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Exception? error = default;
        WriteRequest? operation = default;
        try
        {
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                while (TryDequeue(out operation))
                {
                    using var slice = operation.Buffers.ConsumeSlice(operation.Buffers.Length);
                    foreach (var buffer in slice.MemorySegments)
                    {
                        await Stream.WriteAsync(buffer, _connectionClosingCts.Token);
                    }

                    operation.SetResult();
                }

                await _writerSignal.WaitAsync();
            }
        }
        catch (Exception exception)
        {
            // Don't treat as error if we initiated shutdown
            if (!_connectionClosingCts.IsCancellationRequested && !_streamDisposed)
            {
                error = exception;
            }
        }
        finally
        {
            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
            var requestError = _shutdownReason ?? new ConnectionClosedException();
            operation?.SetException(requestError);

            // Only log unexpected errors (not when we intentionally disposed the stream)
            if (error is not null && !_streamDisposed)
            {
                _logger.LogError(0, error, $"Unexpected exception in {nameof(StreamMessageTransport)}.{nameof(ProcessWrites)}.");
            }

            lock (_writesLock)
            {
                _writesCompleted = true;
                while (_pendingWrites.TryDequeue(out operation))
                {
                    operation.SetException(requestError);
                }
            }
        }

        bool TryDequeue([NotNullWhen(true)] out WriteRequest? operation)
        {
            lock (_writesLock)
            {
                return _pendingWrites.TryDequeue(out operation);
            }
        }
    }
}
