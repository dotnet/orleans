#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Connections;
using Orleans.Connections.Transport;
using Orleans.Messaging;
using Orleans.Runtime.Internal;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime.Messaging
{
    internal abstract partial class Connection : IMessageReceiver
    {
        private readonly ConnectionCommon _shared;
        private readonly TaskCompletionSource _initializationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _startedClosing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _id;
        private readonly MessageTransport _transport;
        private readonly SendWorker _sendWorker;
        private Task? _processIncomingTask;
        private Task? _closeTask;
        private long _lastMessageReceivedTimestamp;

        protected Connection(
            MessageTransport transport,
            ConnectionCommon shared)
        {
            _id = CorrelationIdGenerator.GetNextId();
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _shared = shared;

            _sendWorker = new(this);

            _transport.Closed.Register(static state => ((Connection)state!).OnTransportConnectionClosed(), this);
        }

        public string ConnectionId => _id;

        public EndPoint RemoteEndPoint => _transport.Features.Get<IConnectionEndPointFeature>()?.RemoteEndPoint ?? UnknownEndPoint.Instance;

        public EndPoint LocalEndPoint => _transport.Features.Get<IConnectionEndPointFeature>()?.LocalEndPoint ?? UnknownEndPoint.Instance;

        protected MessageTransport Context => _transport;
        protected ConnectionTrace Log => _shared.ConnectionTrace;
        protected MessagingTrace MessagingTrace => _shared.MessagingTrace;
        protected MessagingInstruments MessagingMetrics => _shared.MessagingInstruments;
        protected NetworkingInstruments NetworkingMetrics => _shared.NetworkingInstruments;
        protected abstract ConnectionDirection ConnectionDirection { get; }
        protected MessageFactory MessageFactory => _shared.MessageFactory;
        protected abstract IMessageCenter MessageCenter { get; }

        /// <summary>
        /// Gets the timeout for gracefully closing the connection.
        /// </summary>
        protected abstract TimeSpan CloseConnectionTimeout { get; }

        public bool IsValid => _closeTask is null;
        public Task Initialized => _initializationTcs.Task;
        public TimeSpan? ElapsedSinceLastMessageReceived
        {
            get
            {
                var timestamp = Volatile.Read(ref _lastMessageReceivedTimestamp);
                return timestamp == 0 ? null : TimeSpan.FromMilliseconds(CoarseStopwatch.GetTimestamp() - timestamp);
            }
        }

        internal void MarkMessageReceived() => Volatile.Write(ref _lastMessageReceivedTimestamp, CoarseStopwatch.GetTimestamp());

        /// <summary>
        /// Start processing this connection.
        /// </summary>
        /// <returns>A <see cref="Task"/> which completes when the connection terminates and has completed processing.</returns>
        public async Task RunAsync()
        {
            Exception? error = default;
            try
            {
                await RunAsyncCore();
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                await CloseAsync(error);
            }
        }

        protected virtual Task RunAsyncCore()
        {
            using (new ExecutionContextSuppressor())
            {
                _processIncomingTask = ProcessIncoming();
            }

            _initializationTcs.TrySetResult();
            return _processIncomingTask;
        }

        /// <summary>
        /// Called immediately prior to transporting a message.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>Whether or not to continue transporting the message.</returns>
        protected abstract bool PrepareMessageForSend(Message msg);

        protected abstract void RetryMessage(Message msg, Exception? ex = null);

        public Task CloseAsync(Exception? exception)
        {
            StartClosing(exception);
            return _closeTask;
        }

        private void OnTransportConnectionClosed()
        {
            StartClosing(new ConnectionClosedException("Underlying connection closed."));
        }

        [MemberNotNull(nameof(_closeTask))]
        private void StartClosing(Exception? exception)
        {
            if (_closeTask is not null)
            {
                return;
            }

            using var _ = new ExecutionContextSuppressor();
            var task = new Task<Task>(CloseAsync);
            if (Interlocked.CompareExchange(ref _closeTask, task.Unwrap(), null) is not null)
            {
                return;
            }

            if (!_initializationTcs.Task.IsCompleted)
            {
                _initializationTcs.TrySetException(exception ?? new ConnectionAbortedException("Connection initialization failed."));
            }

            _initializationTcs.Task.Ignore();

            LogInformationClosingConnection(Log, exception is not ConnectionClosedException ? exception : null, this);
            task.Start(TaskScheduler.Default);
        }

        /// <summary>
        /// Close the connection. This method should only be called by <see cref="StartClosing(Exception)"/>.
        /// </summary>
        private async Task CloseAsync()
        {
            NetworkingMetrics.OnClosedSocket(ConnectionDirection);

            try
            {
                using var timeoutCts = new CancellationTokenSource(CloseConnectionTimeout);
                await _transport.CloseAsync(new ConnectionClosedException(), timeoutCts.Token);
            }
            catch (Exception closeException)
            {
                LogWarningExceptionTerminatingConnection(Log, closeException, this);
            }

            if (_processIncomingTask is { IsCompleted: false } incoming)
            {
                try
                {
                    await incoming;
                }
                catch (Exception processIncomingException)
                {
                    LogWarningExceptionProcessingIncomingMessages(Log, processIncomingException, this);
                }
            }

            try
            {
                await _transport.DisposeAsync();
            }
            catch (Exception abortException)
            {
                LogWarningExceptionTerminatingConnection(Log, abortException, this);
            }

        }

        public virtual void Send(Message message)
        {
            Debug.Assert(!message.IsLocalOnly);
            _sendWorker.Schedule(message);
        }

        private sealed class UnknownEndPoint : EndPoint
        {
            public static UnknownEndPoint Instance { get; } = new();

            public override string ToString() => "unknown";
        }

        private sealed class SendWorker(Connection connection) : IThreadPoolWorkItem
        {
            private const int MaxMessagesPerBatch = 64;
            private const int SoftMaxBatchBytes = 64 * 1024;
            private readonly ConcurrentQueue<Message> _workItems = new();
            private readonly Action<Message>? _messageObserver = connection._shared.MessageObserver;
            private readonly Connection _connection = connection;
            private int _active;

            public void Schedule(Message message)
            {
                _workItems.Enqueue(message);

                if (Interlocked.CompareExchange(ref _active, 1, 0) == 0)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
                }
            }

            void IThreadPoolWorkItem.Execute()
            {
                var writeRequest = _connection._shared.MessageHandlerShared.GetSendMessageHandler(_connection);
                var attempts = 0;
                while (attempts++ < MaxMessagesPerBatch
                    && writeRequest.Length < SoftMaxBatchBytes
                    && !writeRequest.HasLargeMessages
                    && _workItems.TryDequeue(out var message))
                {
                    if (!_connection.PrepareMessageForSend(message))
                    {
                        continue;
                    }

                    try
                    {
                        writeRequest.WriteMessage(message);
                        _messageObserver?.Invoke(message);
                    }
                    catch (Exception exception)
                    {
                        _connection.OnMessageSerializationFailure(message, exception);
                        break;
                    }
                }

                writeRequest.CompleteWriting();
                if (writeRequest.MessageCount == 0)
                {
                    writeRequest.Reset();
                }
                else if (!_connection._transport.EnqueueWrite(writeRequest))
                {
                    _connection.StartClosing(new ConnectionClosedException());
                    for (var i = 0; i < writeRequest.MessageCount; i++)
                    {
                        _connection.RerouteMessage(writeRequest.GetMessage(i));
                    }

                    writeRequest.Reset();
                }

                _active = 0;
                Thread.MemoryBarrier();
                if (!_workItems.IsEmpty && Interlocked.CompareExchange(ref _active, 1, 0) == 0)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
                }
            }

        }

        public override string ToString() => $"{nameof(Connection)}(Id: {_id}, Transport: {_transport})";

        internal protected abstract void OnReceivedMessage(Message message);
        internal protected abstract void RecordMessageReceive(Message message, int totalBytes, int headerBytes);
        internal protected abstract void RecordMessageSend(Message message, int totalBytes, int headerBytes);
        public void OnReadCompleted(Exception error)
        {
            StartClosing(error);
            _startedClosing.TrySetResult();
        }

        public void EnqueueRead()
        {
            var request = _shared.MessageHandlerShared.GetReceiveMessageHandler();
            request.SetConnection(this);
            if (!_transport.EnqueueRead(request))
            {
                request.Reset();
                StartClosing(new ConnectionClosedException());
                _startedClosing.TrySetResult();
            }
        }

        private async Task ProcessIncoming()
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            EnqueueRead();
            await _startedClosing.Task.ConfigureAwait(false);
        }

        internal void RerouteMessage(Message message, Exception? error = null)
        {
            LogInformationReroutingMessage(Log, message, this);

            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                var (connection, msg, exception) = ((Connection, Message, Exception?))state!;
                connection.RetryMessage(msg, exception);
            }, (this, message, error), preferLocal: true);
        }

        private void OnMessageSerializationFailure(Message message, Exception exception)
        {
            LogErrorExceptionSerializingMessage(Log, exception, message, this);

            MessagingMetrics.OnRejectedMessage(message);

            if (message.HasDirection)
            {
                if (message.Direction == Message.Directions.Request)
                {
                    var response = MessageFactory.CreateResponseMessage(message);
                    response.Result = Message.ResponseTypes.Error;
                    response.BodyObject = Response.FromException(exception);
                    try
                    {
                        MessageCenter.DispatchLocalMessage(response);
                    }
                    finally
                    {
                        message.Dispose();
                    }
                }
                else if (message.Direction == Message.Directions.Response && message.RetryCount < MessagingOptions.DEFAULT_MAX_MESSAGE_SEND_RETRIES)
                {
                    message.Result = Message.ResponseTypes.Error;
                    message.BodyObject = Response.FromException(exception);
                    ++message.RetryCount;
                    Send(message);
                }
                else
                {
                    LogWarningDroppingMessage(Log, exception, message);
                    MessagingMetrics.OnDroppedSentMessage(message);
                    message.Dispose();
                }
            }
            else
            {
                message.Dispose();
            }
        }

        public virtual void ReceiveMessage(Message message, IMessageReceiverCache cache)
        {
            if (!IsValid)
            {
                cache.MessageReceiver = null;
                RetryMessage(message);
                return;
            }

            Send(message);
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Closing connection {Connection}"
        )]
        private static partial void LogInformationClosingConnection(ILogger logger, Exception? exception, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception processing incoming messages on connection {Connection}"
        )]
        private static partial void LogWarningExceptionProcessingIncomingMessages(ILogger logger, Exception exception, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception terminating connection {Connection}"
        )]
        private static partial void LogWarningExceptionTerminatingConnection(ILogger logger, Exception exception, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Rerouting message {Message} from connection {Connection}"
        )]
        private static partial void LogInformationReroutingMessage(ILogger logger, Message message, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Exception serializing message {Message} on connection {Connection}"
        )]
        private static partial void LogErrorExceptionSerializingMessage(ILogger logger, Exception exception, Message message, Connection connection);

        [LoggerMessage(
            EventId = (int)ErrorCode.Messaging_OutgoingMS_DroppingMessage,
            Level = LogLevel.Warning,
            Message = "Dropping message which failed during serialization: {Message}"
        )]
        private static partial void LogWarningDroppingMessage(ILogger logger, Exception exception, Message message);
    }
}
