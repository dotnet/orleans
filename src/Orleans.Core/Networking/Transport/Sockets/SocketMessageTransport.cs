#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Sockets;
using System.Diagnostics;
using Orleans.Runtime.Internal;
using System.Net;
using Orleans.Runtime;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Connections.Transport;

namespace Orleans.Connections.Transport.Sockets;

public sealed class SocketMessageTransport : MessageTransportBase
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private readonly SocketSender _socketSender = new();
    private readonly SocketReceiver _socketReceiver = new();
    private readonly Socket _socket;
    private readonly Queue<ReadRequest> _readRequests = new();
    private readonly SingleWaiterAutoResetEvent _readSignal = new() { RunContinuationsAsynchronously = false };
    private readonly SingleWaiterAutoResetEvent _writeSignal = new() { RunContinuationsAsynchronously = false };
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _connectionClosingCts = new();
    private readonly CancellationTokenSource _connectionClosedCts = new();
    private readonly object _shutdownLock = new();
    private readonly object _writesLock = new();
    private readonly object _readsLock = new();
    private readonly string _remoteEndpointString; // For diagnostics only
    private readonly string _localEndpointString; // For diagnostics only
    private Queue<WriteRequest> _writeRequests = new();
    private bool _readsCompleted;
    private bool _writesCompleted;
    private Task? _processingTask;
    private volatile bool _socketDisposed;
    private volatile bool _socketShutdown;
    private volatile Exception? _shutdownReason;

    public SocketMessageTransport(Socket socket, ILogger logger)
    {
        _socket = socket;
        _logger = logger;

        var remoteEndPoint = NormalizeEndpoint(_socket.RemoteEndPoint);
        var localEndPoint = NormalizeEndpoint(_socket.LocalEndPoint);

        Features.Set<IConnectionEndPointFeature>(new ConnectionEndPointFeature
        {
            RemoteEndPoint = remoteEndPoint,
            LocalEndPoint = localEndPoint,
        });

        _remoteEndpointString = remoteEndPoint?.ToString() ?? "null";
        _localEndpointString = localEndPoint?.ToString() ?? "null";
    }

    public override CancellationToken Closed => _connectionClosedCts.Token;

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
            var receiveTask = ProcessReads();
            var sendTask = ProcessWrites();

            // Wait for both to complete
            try
            {
                await receiveTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Unexpected exception in {nameof(SocketMessageTransport)}.{nameof(ProcessReads)}.");
            }

            try
            {
                await sendTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Unexpected exception in {nameof(SocketMessageTransport)}.{nameof(ProcessWrites)}.");
            }

            _socketReceiver.Dispose();
            _socketSender.Dispose();
        }
        catch (Exception ex)
        {
            _shutdownReason ??= ex;
            _logger.LogError(0, ex, $"Unexpected exception in {nameof(SocketMessageTransport)}.{nameof(ProcessConnectionAsync)}.");
        }
        finally
        {
            Shutdown();

            _connectionClosingCts.Cancel();
            _connectionClosedCts.Cancel();
        }
    }

    private void Shutdown()
    {
        if (_socketDisposed)
        {
            return;
        }

        lock (_shutdownLock)
        {
            try
            {
                if (_socketDisposed)
                {
                    return;
                }

                _socketDisposed = true;

                // shutdownReason should only be null if the output was completed gracefully, so no one should ever
                // ever observe the nondescript ConnectionAbortedException except for connection middleware attempting
                // to half close the connection which is currently unsupported.
                _shutdownReason ??= new ConnectionAbortedException("The Socket transport's send loop completed gracefully.");
                SocketsLog.ConnectionWriteFin(_logger, this, _shutdownReason.Message);

                // Only call Shutdown if we haven't already done so
                if (!_socketShutdown)
                {
                    _socketShutdown = true;
                    try
                    {
                        _socket.Shutdown(SocketShutdown.Both);
                    }
                    catch
                    {
                        // Ignore any errors from Socket.Shutdown() since we're tearing down the connection anyway.
                    }
                }

                _socket.Dispose();
            }
            catch (Exception exception)
            {
                SocketsLog.ConnectionShutdownError(_logger, this, exception);
            }
        }
    }

    /// <summary>
    /// Initiates a graceful shutdown of the socket to interrupt pending I/O operations.
    /// This does not dispose the socket - that happens in <see cref="Shutdown"/>.
    /// </summary>
    private void ShutdownSocket()
    {
        if (_socketShutdown || _socketDisposed)
        {
            return;
        }

        lock (_shutdownLock)
        {
            if (_socketShutdown || _socketDisposed)
            {
                return;
            }

            _socketShutdown = true;

            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Ignore errors - socket may already be in error state or not connected
            }
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

    public override async ValueTask CloseAsync(Exception? closeReason, CancellationToken cancellationToken = default)
    {
        _shutdownReason ??= closeReason;

        // Early exit if already closed
        if (_connectionClosedCts.IsCancellationRequested)
        {
            return;
        }

        // Signal loops to stop
        _connectionClosingCts.Cancel();
        _readSignal.Signal();
        _writeSignal.Signal();

        // If no processing task, just dispose the socket directly
        if (_processingTask is null)
        {
            Shutdown();
            return;
        }

        // Shutdown the socket to interrupt any pending I/O operations.
        // This will cause ReceiveAsync/SendAsync to complete with an error,
        // allowing the processing loops to exit gracefully.
        ShutdownSocket();

        // Wait for processing task to complete (which will dispose the socket in its finally block)
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
            // Cancellation requested - force shutdown immediately
            Shutdown();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        // Ensure socket is shutdown and disposed, even if CloseAsync wasn't called or timed out
        Shutdown();

        // Signal that we're closing if not already done
        _connectionClosingCts.Cancel();
        _connectionClosedCts.Cancel();

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ProcessReads()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var isGracefulTermination = false;
        Exception? error = null;
        ReadRequest? request = null;
        using ArcBufferWriter bufferWriter = new();
        var reader = new ArcBufferReader(bufferWriter);

        // Note that socket APIs can generally only accept a maximum number of buffers.
        // For example on Linux, the maximum is defined via IOV_MAX in <limits.h> and is typically 16.
        // See https://www.man7.org/linux/man-pages/man0/limits.h.0p.html
        // Here, we choose 8 as the maximum number of buffers which CoreCLR will stackalloc on *nix,
        // see: https://github.com/dotnet/runtime/blob/0cf461b302f58c7add3f6dc405873fb2212b513f/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketPal.Unix.cs#L24
        List<ArraySegment<byte>> networkBuffers = new(capacity: 8);

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
                        if (request.OnRead(reader))
                        {
                            // This request is complete, move on to the next one.
                            break;
                        }

                        bufferWriter.ReplenishBuffers(networkBuffers);
                        Debug.Assert(networkBuffers.Count == networkBuffers.Capacity);
                        await _socketReceiver.ReceiveAsync(_socket, networkBuffers).ConfigureAwait(false);

                        if (_socketReceiver.HasError)
                        {
                            error = _socketReceiver.Error;
                            isGracefulTermination = HandleReadError(ref error);
                            goto exit;
                        }

                        var transferred = _socketReceiver.BytesTransferred;

                        MaintainBufferList(networkBuffers, transferred);
                        bufferWriter.AdvanceWriter(transferred);

                        if (transferred == 0)
                        {
                            // FIN
                            SocketsLog.ConnectionReadFin(_logger, this);
                            isGracefulTermination = true;
                            goto exit;
                        }
                    }
                }

                await _readSignal.WaitAsync().ConfigureAwait(false);
            }

            isGracefulTermination = true;
exit:
            /* no op */;
        }
        catch (Exception exception)
        {
            if (_connectionClosingCts.IsCancellationRequested)
            {
                isGracefulTermination = true;
            }
            else
            {
                error = exception;
                isGracefulTermination = HandleReadError(ref error);
            }
        }
        finally
        {
            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();

            if (isGracefulTermination)
            {
                request?.OnCanceled();
            }
            else
            {
                Debug.Assert(error is not null);
                request?.OnError(error);
            }

            _writeSignal.Signal();

            lock (_readsLock)
            {
                _readsCompleted = true;
                while (_readRequests.TryDequeue(out request))
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
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool TryDequeue([NotNullWhen(true)] out ReadRequest? request)
        {
            lock (_readsLock)
            {
                return _readRequests.TryDequeue(out request);
            }
        }

        static void MaintainBufferList(List<ArraySegment<byte>> buffers, int readSize)
        {
            while (readSize > 0)
            {
                Debug.Assert(buffers.Count > 0);
                var bufferSize = buffers[0].Count;
                if (bufferSize <= readSize)
                {
                    // Consume the buffer completely.
                    readSize -= bufferSize;
                    buffers.RemoveAt(0);
                }
                else
                {
                    // Consume the buffer partially.
                    buffers[0] = new(buffers[0].Array!, buffers[0].Offset + readSize, bufferSize - readSize);
                    Debug.Assert(buffers[0].Count > 0);
                    break;
                }
            }
        }
    }

    private bool HandleReadError(ref Exception? error)
    {
        if (_socketReceiver.HasError)
        {
            error = _socketReceiver.Error;
        }

        // If we initiated shutdown, treat any error as graceful termination
        if (_socketDisposed || _socketShutdown)
        {
            error = null;
            return true;
        }

        if (error is ObjectDisposedException)
        {
            // This is unexpected if the socket hasn't been disposed yet.
            SocketsLog.ConnectionError(_logger, this, error);
        }
        else if (IsConnectionResetError(_socketReceiver.SocketError))
        {
            // This could be ignored if _shutdownReason is already set.
            error = null;

            // There's still a small chance that both DoReceive() and DoSend() can log the same connection reset.
            // Both logs will have the same ConnectionId. I don't think it's worthwhile to lock just to avoid this.
            SocketsLog.ConnectionReset(_logger, this);
        }
        else if (IsConnectionAbortError(_socketReceiver.SocketError))
        {
            // This exception should always be ignored because _shutdownReason should be set.
            error = null;
        }
        else if (error is { })
        {
            // This is unexpected.
            error = _socketReceiver.Error!;
            SocketsLog.ConnectionError(_logger, this, error);
        }
        else
        {
            error = null;
        }

        return error is null;
    }

    private async Task ProcessWrites()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        const int MaxBuffersPerSend = 32;
        Exception? error = null;
        Queue<WriteRequest> requests = new();
        List<ArraySegment<byte>> buffers = new(capacity: MaxBuffersPerSend);
        List<(WriteRequest, ArcBuffer)> processingRequests = new(capacity: MaxBuffersPerSend);
        ArcBuffer.ArraySegmentEnumerator enumerator = default;

        try
        {
            // Loop until termination.
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                while (buffers.Count < MaxBuffersPerSend)
                {
                    // Try to consume a buffer from the current enumerator.
                    if (enumerator.MoveNext())
                    {
                        Debug.Assert(enumerator.Current.Count > 0);
                        buffers.Add(enumerator.Current);
                    }
                    else
                    {
DequeueRequest:
                        // Try to get the next request and consume that.
                        if (requests.TryDequeue(out var request))
                        {
                            // Start enumerating the next request.
                            var slice = request.Buffers.ConsumeSlice(request.Buffers.Length);
                            processingRequests.Add((request, slice));
                            enumerator = slice.ArraySegments;
                        }
                        else if (buffers.Count == 0)
                        {
RefreshRequestQueue:
                            if (_connectionClosingCts.IsCancellationRequested)
                            {
                                break;
                            }

                            // Check for pending messages before waiting.
                            RefreshRequestQueue(ref requests);

                            // Wait for more requests.
                            if (requests.Count == 0)
                            {
                                await _writeSignal.WaitAsync().ConfigureAwait(false);
                                goto RefreshRequestQueue;
                            }

                            goto DequeueRequest;
                        }
                        else
                        {
                            // Send the current buffers.
                            enumerator = default;
                            break;
                        }
                    }
                }

                // If there are no buffers to send, continue to check for more requests or exit
                if (buffers.Count == 0)
                {
                    continue;
                }

                await _socketSender.SendAsync(_socket, buffers).ConfigureAwait(false);
                buffers.Clear();

                if (_socketSender.HasError)
                {
                    error = GetSendAsyncError();
                    break;
                }

                // Signal that the requests are completed
                for (var i = 0; i < processingRequests.Count - 1; i++)
                {
                    var (request, slice) = processingRequests[i];
                    request.SetResult();
                    slice.Dispose();
                }

                var last = processingRequests[^1];
                processingRequests.Clear();

                // Avoid disposing the last item unless enumeration has completed.
                if (enumerator.IsCompleted)
                {
                    var (request, slice) = last;
                    request.SetResult();
                    slice.Dispose();
                }
                else
                {
                    processingRequests.Add(last);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
            if (!_socketDisposed)
            {
                SocketsLog.ConnectionError(_logger, this, error);
            }
        }
        finally
        {
            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
            _readSignal.Signal();

            var requestError = _shutdownReason ?? new ConnectionClosedException();
            foreach (var (request, slice) in processingRequests)
            {
                request.SetException(requestError);
                slice.Dispose();
            }

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

    private Exception GetSendAsyncError()
    {
        Exception error;
        if (IsConnectionResetError(_socketSender.SocketError))
        {
            // This could be ignored if _shutdownReason is already set.
            var ex = _socketSender.Error!;
            error = new ConnectionResetException(ex.Message, ex);

            // There's still a small chance that both DoReceive() and DoSend() can log the same connection reset.
            // Both logs will have the same ConnectionId. I don't think it's worthwhile to lock just to avoid this.
            if (!_socketDisposed)
            {
                SocketsLog.ConnectionReset(_logger, this);
            }
        }
        else if (IsConnectionAbortError(_socketSender.SocketError))
        {
            // This exception should always be ignored because _shutdownReason should be set.
            error = _socketSender.Error!;

            if (!_socketDisposed)
            {
                // This is unexpected if the socket hasn't been disposed yet.
                SocketsLog.ConnectionError(_logger, this, error);
            }
        }
        else
        {
            // This is unexpected.
            error = _socketSender.Error!;
            if (!_socketDisposed)
            {
                SocketsLog.ConnectionError(_logger, this, error);
            }
        }

        return error;
    }

    private static bool IsConnectionResetError(SocketError errorCode)
    {
        // A connection reset can be reported as SocketError.ConnectionAborted on Windows.
        // ProtocolType can be removed once https://github.com/dotnet/corefx/issues/31927 is fixed.
        return errorCode == SocketError.ConnectionReset ||
               errorCode == SocketError.Shutdown ||
               errorCode == SocketError.ConnectionAborted && IsWindows ||
               errorCode == SocketError.ProtocolType && IsMacOS;
    }

    private static bool IsConnectionAbortError(SocketError errorCode)
    {
        // Calling Dispose after ReceiveAsync can cause an "InvalidArgument" error on *nix.
        return errorCode == SocketError.OperationAborted ||
               errorCode == SocketError.Interrupted ||
               errorCode == SocketError.InvalidArgument && !IsWindows;
    }

    private static EndPoint? NormalizeEndpoint(EndPoint? endpoint)
    {
        if (endpoint is not IPEndPoint ep) return endpoint;

        // Normalize endpoints
        if (ep.Address.IsIPv4MappedToIPv6)
        {
            return new IPEndPoint(ep.Address.MapToIPv4(), ep.Port);
        }

        return ep;
    }

    public override string ToString() => $"Socket(Remote: {_remoteEndpointString}, Local: {_localEndpointString})";
}
