using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Orleans.Networking.Shared;

namespace Orleans.Networking.Shared
{
    internal sealed class SocketConnection : TransportConnection
    {
        private static readonly int MinAllocBufferSize = PinnedBlockMemoryPool.BlockSize / 2;
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        private readonly Socket _socket;
        private readonly ISocketsTrace _trace;
        private readonly SocketReceiver _receiver;
        private readonly SocketSender _sender;
        private readonly CancellationTokenSource _connectionClosedTokenSource = new CancellationTokenSource();

        private readonly object _shutdownLock = new object();
        private volatile bool _socketDisposed;
        private volatile Exception _shutdownReason;
        private Task _processingTask;
        private readonly TaskCompletionSource<object> _waitForConnectionClosedTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _connectionClosed;

        internal SocketConnection(Socket socket,
                                  MemoryPool<byte> memoryPool,
                                  PipeScheduler scheduler,
                                  ISocketsTrace trace,
                                  long? maxReadBufferSize = null,
                                  long? maxWriteBufferSize = null)
        {
            Debug.Assert(socket != null);
            Debug.Assert(memoryPool != null);
            Debug.Assert(trace != null);

            _socket = socket;
            MemoryPool = memoryPool;
            _trace = trace;

            LocalEndPoint = _socket.LocalEndPoint;
            RemoteEndPoint = _socket.RemoteEndPoint;

            ConnectionClosed = _connectionClosedTokenSource.Token;

            // On *nix platforms, Sockets already dispatches to the ThreadPool.
            // Yes, the IOQueues are still used for the PipeSchedulers. This is intentional.
            // https://github.com/aspnet/KestrelHttpServer/issues/2573
            var awaiterScheduler = IsWindows ? scheduler : PipeScheduler.Inline;

            _receiver = new SocketReceiver(awaiterScheduler);
            _sender = new SocketSender(awaiterScheduler);

            maxReadBufferSize ??= 0;
            maxWriteBufferSize ??= 0;

            var inputOptions = new PipeOptions(MemoryPool, PipeScheduler.ThreadPool, scheduler, maxReadBufferSize.Value, maxReadBufferSize.Value / 2, useSynchronizationContext: false);
            var outputOptions = new PipeOptions(MemoryPool, scheduler, PipeScheduler.ThreadPool, maxWriteBufferSize.Value, maxWriteBufferSize.Value / 2, useSynchronizationContext: false);

            var pair = DuplexPipe.CreateConnectionPair(inputOptions, outputOptions);

            // Set the transport and connection id
            Transport = pair.Transport;
            Application = pair.Application;
        }

        public PipeWriter Input => Application.Output;

        public PipeReader Output => Application.Input;

        public override MemoryPool<byte> MemoryPool { get; }

        public void Start()
        {
            _processingTask = StartAsync();
        }

        private async Task StartAsync()
        {
            try
            {
                // Spawn send and receive logic
                var receiveTask = DoReceive();
                var sendTask = DoSend();

                // Now wait for both to complete
                await receiveTask;
                await sendTask;

                _receiver.Dispose();
                _sender.Dispose();
            }
            catch (Exception ex)
            {
                _trace.LogError(0, ex, $"Unexpected exception in {nameof(SocketConnection)}.{nameof(StartAsync)}.");
            }
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            // Try to gracefully close the socket to match libuv behavior.
            Shutdown(abortReason);

            // Cancel ProcessSends loop after calling shutdown to ensure the correct _shutdownReason gets set.
            Output.CancelPendingRead();
        }

        // Only called after connection middleware is complete which means the ConnectionClosed token has fired.
        public override async ValueTask DisposeAsync()
        {
            Transport.Input.Complete();
            Transport.Output.Complete();

            if (_processingTask != null)
            {
                await _processingTask;
            }

            _connectionClosedTokenSource.Dispose();
        }

        private async Task DoReceive()
        {
            Exception error = null;

            try
            {
                while (true)
                {
                    // Wait for data before allocating a buffer.
                    var waitForDataResult = await _receiver.WaitForDataAsync(_socket);

                    if (!IsNormalCompletion(waitForDataResult))
                    {
                        break;
                    }

                    // Ensure we have some reasonable amount of buffer space
                    var buffer = Input.GetMemory(MinAllocBufferSize);

                    var receiveResult = await _receiver.ReceiveAsync(_socket, buffer);

                    if (!IsNormalCompletion(receiveResult))
                    {
                        break;
                    }

                    var bytesReceived = receiveResult.BytesTransferred;
                    if (bytesReceived == 0)
                    {
                        // FIN
                        _trace.ConnectionReadFin(ConnectionId);
                        break;
                    }

                    Input.Advance(bytesReceived);

                    var flushTask = Input.FlushAsync();

                    var paused = !flushTask.IsCompleted;

                    if (paused)
                    {
                        _trace.ConnectionPause(ConnectionId);
                    }

                    var result = await flushTask;

                    if (paused)
                    {
                        _trace.ConnectionResume(ConnectionId);
                    }

                    if (result.IsCompleted || result.IsCanceled)
                    {
                        // Pipe consumer is shut down, do we stop writing
                        break;
                    }

                    bool IsNormalCompletion(SocketOperationResult result)
                    {
                        if (!result.HasError)
                        {
                            return true;
                        }

                        if (IsConnectionResetError(result.SocketError.SocketErrorCode))
                        {
                            // This could be ignored if _shutdownReason is already set.
                            var ex = result.SocketError;
                            error = new ConnectionResetException(ex.Message, ex);

                            // There's still a small chance that both DoReceive() and DoSend() can log the same connection reset.
                            // Both logs will have the same ConnectionId. I don't think it's worthwhile to lock just to avoid this.
                            if (!_socketDisposed)
                            {
                                SocketsLog.ConnectionReset(_trace, this);
                            }

                            return false;
                        }

                        if (IsConnectionAbortError(result.SocketError.SocketErrorCode))
                        {
                            // This exception should always be ignored because _shutdownReason should be set.
                            error = result.SocketError;

                            if (!_socketDisposed)
                            {
                                // This is unexpected if the socket hasn't been disposed yet.
                                SocketsLog.ConnectionError(_trace, this, error);
                            }

                            return false;
                        }

                        // This is unexpected.
                        error = result.SocketError;
                        SocketsLog.ConnectionError(_trace, this, error);

                        return false;
                    }
                }
            }
            catch (ObjectDisposedException ex)
            {
                // This exception should always be ignored because _shutdownReason should be set.
                error = ex;

                if (!_socketDisposed)
                {
                    // This is unexpected if the socket hasn't been disposed yet.
                    SocketsLog.ConnectionError(_trace, this, error);
                }
            }
            catch (Exception ex)
            {
                // This is unexpected.
                error = ex;
                SocketsLog.ConnectionError(_trace, this, error);
            }
            finally
            {
                // If Shutdown() has already been called, assume that was the reason ProcessReceives() exited.
                Input.Complete(_shutdownReason ?? error);

                FireConnectionClosed();

                await _waitForConnectionClosedTcs.Task;
            }
        }

        private async Task DoSend()
        {
            Exception shutdownReason = null;
            Exception unexpectedError = null;

            try
            {
                while (true)
                {
                    var result = await Output.ReadAsync();

                    if (result.IsCanceled)
                    {
                        break;
                    }

                    var buffer = result.Buffer;

                    if (!buffer.IsEmpty)
                    {
                        var transferResult = await _sender.SendAsync(_socket, buffer);

                        if (transferResult.HasError)
                        {
                            if (IsConnectionResetError(transferResult.SocketError.SocketErrorCode))
                            {
                                var ex = transferResult.SocketError;
                                shutdownReason = new ConnectionResetException(ex.Message, ex);
                                SocketsLog.ConnectionReset(_trace, this);

                                break;
                            }

                            if (IsConnectionAbortError(transferResult.SocketError.SocketErrorCode))
                            {
                                shutdownReason = transferResult.SocketError;

                                break;
                            }

                            unexpectedError = shutdownReason = transferResult.SocketError;
                        }
                    }

                    Output.AdvanceTo(buffer.End);

                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (ObjectDisposedException ex)
            {
                // This should always be ignored since Shutdown() must have already been called by Abort().
                shutdownReason = ex;
            }
            catch (Exception ex)
            {
                shutdownReason = ex;
                unexpectedError = ex;
                SocketsLog.ConnectionError(_trace, this, unexpectedError);
            }
            finally
            {
                Shutdown(shutdownReason);

                // Complete the output after disposing the socket
                Output.Complete(unexpectedError);

                // Cancel any pending flushes so that the input loop is un-paused
                Input.CancelPendingFlush();
            }
        }

        private void FireConnectionClosed()
        {
            // Guard against scheduling this multiple times
            if (_connectionClosed)
            {
                return;
            }

            _connectionClosed = true;

            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                ((SocketConnection)state).CancelConnectionClosedToken();

                ((SocketConnection)state)._waitForConnectionClosedTcs.TrySetResult(null);
            },
            this);
        }

        private void Shutdown(Exception shutdownReason)
        {
            lock (_shutdownLock)
            {
                if (_socketDisposed)
                {
                    return;
                }

                // Make sure to close the connection only after the _aborted flag is set.
                // Without this, the RequestsCanBeAbortedMidRead test will sometimes fail when
                // a BadHttpRequestException is thrown instead of a TaskCanceledException.
                _socketDisposed = true;

                // shutdownReason should only be null if the output was completed gracefully, so no one should ever
                // ever observe the nondescript ConnectionAbortedException except for connection middleware attempting
                // to half close the connection which is currently unsupported.
                _shutdownReason = shutdownReason ?? new ConnectionAbortedException("The Socket transport's send loop completed gracefully.");

                _trace.ConnectionWriteFin(ConnectionId, _shutdownReason.Message);

                try
                {
                    // Try to gracefully close the socket even for aborts to match libuv behavior.
                    _socket.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                    // Ignore any errors from Socket.Shutdown() since we're tearing down the connection anyway.
                }

                _socket.Dispose();
            }
        }

        private void CancelConnectionClosedToken()
        {
            try
            {
                _connectionClosedTokenSource.Cancel();
            }
            catch (Exception ex)
            {
                _trace.LogError(0, ex, $"Unexpected exception in {nameof(SocketConnection)}.{nameof(CancelConnectionClosedToken)}.");
            }
        }

        private static bool IsConnectionResetError(SocketError errorCode)
        {
            // A connection reset can be reported as SocketError.ConnectionAborted on Windows.
            // ProtocolType can be removed once https://github.com/dotnet/corefx/issues/31927 is fixed.
            return errorCode == SocketError.ConnectionReset ||
                   errorCode == SocketError.Shutdown ||
                   (errorCode == SocketError.ConnectionAborted && IsWindows) ||
                   (errorCode == SocketError.ProtocolType && IsMacOS);
        }

        private static bool IsConnectionAbortError(SocketError errorCode)
        {
            // Calling Dispose after ReceiveAsync can cause an "InvalidArgument" error on *nix.
            return errorCode == SocketError.OperationAborted ||
                   errorCode == SocketError.Interrupted ||
                   (errorCode == SocketError.InvalidArgument && !IsWindows);
        }
    }

    internal static partial class SocketsLog
    {
        // Reserved: Event ID 3, EventName = ConnectionRead

        [LoggerMessage(6, LogLevel.Debug, @"Connection id ""{ConnectionId}"" received FIN.", EventName = "ConnectionReadFin", SkipEnabledCheck = true)]
        private static partial void ConnectionReadFinCore(ILogger logger, string connectionId);

        public static void ConnectionReadFin(ILogger logger, SocketConnection connection)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                ConnectionReadFinCore(logger, connection.ConnectionId);
            }
        }

        [LoggerMessage(7, LogLevel.Debug, @"Connection id ""{ConnectionId}"" sending FIN because: ""{Reason}""", EventName = "ConnectionWriteFin", SkipEnabledCheck = true)]
        private static partial void ConnectionWriteFinCore(ILogger logger, string connectionId, string reason);

        public static void ConnectionWriteFin(ILogger logger, SocketConnection connection, string reason)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                ConnectionWriteFinCore(logger, connection.ConnectionId, reason);
            }
        }

        // Reserved: Event ID 11, EventName = ConnectionWrite

        // Reserved: Event ID 12, EventName = ConnectionWriteCallback

        [LoggerMessage(14, LogLevel.Debug, @"Connection id ""{ConnectionId}"" communication error.", EventName = "ConnectionError", SkipEnabledCheck = true)]
        private static partial void ConnectionErrorCore(ILogger logger, string connectionId, Exception ex);

        public static void ConnectionError(ILogger logger, SocketConnection connection, Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                ConnectionErrorCore(logger, connection.ConnectionId, ex);
            }
        }

        [LoggerMessage(19, LogLevel.Debug, @"Connection id ""{ConnectionId}"" reset.", EventName = "ConnectionReset", SkipEnabledCheck = true)]
        public static partial void ConnectionReset(ILogger logger, string connectionId);

        public static void ConnectionReset(ILogger logger, SocketConnection connection)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                ConnectionReset(logger, connection.ConnectionId);
            }
        }

        [LoggerMessage(4, LogLevel.Debug, @"Connection id ""{ConnectionId}"" paused.", EventName = "ConnectionPause", SkipEnabledCheck = true)]
        private static partial void ConnectionPauseCore(ILogger logger, string connectionId);

        public static void ConnectionPause(ILogger logger, SocketConnection connection)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                ConnectionPauseCore(logger, connection.ConnectionId);
            }
        }

        [LoggerMessage(5, LogLevel.Debug, @"Connection id ""{ConnectionId}"" resumed.", EventName = "ConnectionResume", SkipEnabledCheck = true)]
        private static partial void ConnectionResumeCore(ILogger logger, string connectionId);

        public static void ConnectionResume(ILogger logger, SocketConnection connection)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                ConnectionResumeCore(logger, connection.ConnectionId);
            }
        }
    }
}
