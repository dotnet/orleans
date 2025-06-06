using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime.Messaging
{
    internal abstract partial class Connection
    {
        private static readonly Func<ConnectionContext, Task> OnConnectedDelegate = context => OnConnectedAsync(context);
        private static readonly Action<object> OnConnectionClosedDelegate = state => ((Connection)state).OnTransportConnectionClosed();
        private static readonly UnboundedChannelOptions OutgoingMessageChannelOptions = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

        private static readonly ObjectPool<MessageHandler> MessageHandlerPool = ObjectPool.Create(new MessageHandlerPoolPolicy());
        private readonly ConnectionCommon shared;
        private readonly ConnectionDelegate middleware;
        private readonly Channel<Message> outgoingMessages;
        private readonly ChannelWriter<Message> outgoingMessageWriter;
        private readonly List<Message> inflight = new List<Message>(4);
        private readonly TaskCompletionSource<int> _transportConnectionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _initializationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDuplexPipe _transport;
        private Task _processIncomingTask;
        private Task _processOutgoingTask;
        private Task _closeTask;

        protected Connection(
            ConnectionContext connection,
            ConnectionDelegate middleware,
            ConnectionCommon shared)
        {
            this.Context = connection ?? throw new ArgumentNullException(nameof(connection));
            this.middleware = middleware ?? throw new ArgumentNullException(nameof(middleware));
            this.shared = shared;
            this.outgoingMessages = Channel.CreateUnbounded<Message>(OutgoingMessageChannelOptions);
            this.outgoingMessageWriter = this.outgoingMessages.Writer;

            // Set the connection on the connection context so that it can be retrieved by the middleware.
            this.Context.Features.Set<Connection>(this);

            this.RemoteEndPoint = NormalizeEndpoint(this.Context.RemoteEndPoint);
            this.LocalEndPoint = NormalizeEndpoint(this.Context.LocalEndPoint);
        }

        public ConnectionCommon Shared => shared;
        public string ConnectionId => this.Context?.ConnectionId;
        public virtual EndPoint RemoteEndPoint { get; }
        public virtual EndPoint LocalEndPoint { get; }
        protected ConnectionContext Context { get; }
        protected NetworkingTrace Log => this.shared.NetworkingTrace;
        protected MessagingTrace MessagingTrace => this.shared.MessagingTrace;
        protected abstract ConnectionDirection ConnectionDirection { get; }
        protected MessageFactory MessageFactory => this.shared.MessageFactory;
        protected abstract IMessageCenter MessageCenter { get; }

        public bool IsValid => _closeTask is null;

        public Task Initialized => _initializationTcs.Task;

        public static void ConfigureBuilder(ConnectionBuilder builder) => builder.Run(OnConnectedDelegate);

        /// <summary>
        /// Start processing this connection.
        /// </summary>
        /// <returns>A <see cref="Task"/> which completes when the connection terminates and has completed processing.</returns>
        public async Task Run()
        {
            Exception error = default;
            try
            {
                // Eventually calls through to OnConnectedAsync (unless the connection delegate has been misconfigured)
                await this.middleware(this.Context);
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                await this.CloseAsync(error);
            }
        }

        private static Task OnConnectedAsync(ConnectionContext context)
        {
            var connection = context.Features.Get<Connection>();
            context.ConnectionClosed.Register(OnConnectionClosedDelegate, connection);

            NetworkingInstruments.OnOpenedSocket(connection.ConnectionDirection);
            return connection.RunInternal();
        }

        protected virtual async Task RunInternal()
        {
            _transport = this.Context.Transport;
            _processIncomingTask = this.ProcessIncoming();
            _processOutgoingTask = this.ProcessOutgoing();
            _initializationTcs.TrySetResult(0);
            await Task.WhenAll(_processIncomingTask, _processOutgoingTask);
        }

        /// <summary>
        /// Called immediately prior to transporting a message.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>Whether or not to continue transporting the message.</returns>
        protected abstract bool PrepareMessageForSend(Message msg);

        protected abstract void RetryMessage(Message msg, Exception ex = null);

        public Task CloseAsync(Exception exception)
        {
            StartClosing(exception);
            return _closeTask;
        }

        private void OnTransportConnectionClosed()
        {
            StartClosing(new ConnectionAbortedException("Underlying connection closed"));
            _transportConnectionClosed.SetResult(0);
        }

        private void StartClosing(Exception exception)
        {
            if (_closeTask is not null)
            {
                return;
            }

            var task = new Task<Task>(CloseAsync);
            if (Interlocked.CompareExchange(ref _closeTask, task.Unwrap(), null) is not null)
            {
                return;
            }

            _initializationTcs.TrySetException(exception ?? new ConnectionAbortedException("Connection initialization failed"));
            _initializationTcs.Task.Ignore();

            LogInformationClosingConnection(this.Log, exception, this);

            task.Start(TaskScheduler.Default);
        }

        /// <summary>
        /// Close the connection. This method should only be called by <see cref="StartClosing(Exception)"/>.
        /// </summary>
        private async Task CloseAsync()
        {
            NetworkingInstruments.OnClosedSocket(this.ConnectionDirection);

            // Signal the outgoing message processor to exit gracefully.
            this.outgoingMessageWriter.TryComplete();

            var transportFeature = Context.Features.Get<IUnderlyingTransportFeature>();
            var transport = transportFeature?.Transport ?? _transport;
            transport.Input.CancelPendingRead();
            transport.Output.CancelPendingFlush();

            // Try to gracefully stop the reader/writer loops, if they are running.
            if (_processIncomingTask is { IsCompleted: false } incoming)
            {
                try
                {
                    await incoming;
                }
                catch (Exception processIncomingException)
                {
                    // Swallow any exceptions here.
                    LogWarningExceptionProcessingIncomingMessages(this.Log, processIncomingException, this);
                }
            }

            if (_processOutgoingTask is { IsCompleted: false } outgoing)
            {
                try
                {
                    await outgoing;
                }
                catch (Exception processOutgoingException)
                {
                    // Swallow any exceptions here.
                    LogWarningExceptionProcessingOutgoingMessages(this.Log, processOutgoingException, this);
                }
            }

            // Only wait for the transport to close if the connection actually started being processed.
            if (_processIncomingTask is not null && _processOutgoingTask is not null)
            {
                // Abort the connection and wait for the transport to signal that it's closed before disposing it.
                try
                {
                    this.Context.Abort();
                }
                catch (Exception exception)
                {
                    LogWarningExceptionAbortingConnection(this.Log, exception, this);
                }

                await _transportConnectionClosed.Task;
            }

            try
            {
                await this.Context.DisposeAsync();
            }
            catch (Exception abortException)
            {
                // Swallow any exceptions here.
                LogWarningExceptionTerminatingConnection(this.Log, abortException, this);
            }

            // Reject in-flight messages.
            foreach (var message in this.inflight)
            {
                this.OnSendMessageFailure(message, "Connection terminated");
            }

            this.inflight.Clear();

            // Reroute enqueued messages.
            var i = 0;
            while (this.outgoingMessages.Reader.TryRead(out var message))
            {
                if (i == 0)
                {
                    LogInformationReroutingMessages(this.Log, new EndPointLogValue(this.RemoteEndPoint));
                }

                ++i;
                this.RetryMessage(message);
            }

            if (i > 0)
            {
                LogInformationReroutedMessages(this.Log, i, new EndPointLogValue(this.RemoteEndPoint));
            }
        }

        public virtual void Send(Message message)
        {
            Debug.Assert(!message.IsLocalOnly);
            if (!this.outgoingMessageWriter.TryWrite(message))
            {
                this.RerouteMessage(message);
            }
        }

        public override string ToString() => $"[Local: {this.LocalEndPoint}, Remote: {this.RemoteEndPoint}, ConnectionId: {this.Context.ConnectionId}]";

        protected abstract void RecordMessageReceive(Message msg, int numTotalBytes, int headerBytes);
        protected abstract void RecordMessageSend(Message msg, int numTotalBytes, int headerBytes);
        protected abstract void OnReceivedMessage(Message message);
        protected abstract void OnSendMessageFailure(Message message, string error);

        private async Task ProcessIncoming()
        {
            await Task.Yield();

            Exception error = default;
            var serializer = this.shared.ServiceProvider.GetRequiredService<MessageSerializer>();
            try
            {
                var input = this._transport.Input;
                var requiredBytes = 0;
                while (true)
                {
                    var readResult = await input.ReadAsync();

                    var buffer = readResult.Buffer;
                    if (buffer.Length >= requiredBytes)
                    {
                        do
                        {
                            Message message = default;
                            try
                            {
                                int headerLength, bodyLength;
                                (requiredBytes, headerLength, bodyLength) = serializer.TryRead(ref buffer, out message);
                                if (requiredBytes == 0)
                                {
                                    Debug.Assert(message is not null);
                                    RecordMessageReceive(message, bodyLength + headerLength, headerLength);
                                    var handler = MessageHandlerPool.Get();
                                    handler.Set(message, this);
                                    ThreadPool.UnsafeQueueUserWorkItem(handler, preferLocal: true);
                                }
                            }
                            catch (Exception exception)
                            {
                                if (!HandleReceiveMessageFailure(message, exception))
                                {
                                    throw;
                                }   
                            }
                        } while (requiredBytes == 0);
                    }

                    if (readResult.IsCanceled || readResult.IsCompleted)
                    {
                        break;
                    }

                    input.AdvanceTo(buffer.Start, buffer.End);
                }
            }
            catch (Exception exception)
            {
                if (IsValid)
                {
                    LogWarningExceptionProcessingMessagesFromRemote(this.Log, exception, this.RemoteEndPoint);
                }

                error = exception;
            }
            finally
            {
                _transport.Input.Complete();
                this.StartClosing(error);
            }
        }

        private async Task ProcessOutgoing()
        {
            await Task.Yield();

            Exception error = default;
            var serializer = this.shared.ServiceProvider.GetRequiredService<MessageSerializer>();
            var messageObserver = this.shared.MessageStatisticsSink.GetMessageObserver();
            try
            {
                var output = this._transport.Output;
                var reader = this.outgoingMessages.Reader;

                while (true)
                {
                    var more = await reader.WaitToReadAsync();
                    if (!more)
                    {
                        break;
                    }

                    Message message = default;
                    try
                    {
                        while (inflight.Count < inflight.Capacity && reader.TryRead(out message) && this.PrepareMessageForSend(message))
                        {
                            inflight.Add(message);
                            var (headerLength, bodyLength) = serializer.Write(output, message);
                            RecordMessageSend(message, headerLength + bodyLength, headerLength);
                            messageObserver?.Invoke(message);
                            message = null;
                        }
                    }
                    catch (Exception exception)
                    {
                        if (!HandleSendMessageFailure(message, exception))
                        {
                            throw;
                        }
                    }

                    var flushResult = await output.FlushAsync();
                    if (flushResult.IsCompleted || flushResult.IsCanceled)
                    {
                        break;
                    }

                    inflight.Clear();
                }
            }
            catch (Exception exception)
            {
                if (IsValid)
                {
                    LogWarningExceptionProcessingMessagesToRemote(this.Log, exception, this.RemoteEndPoint);
                }

                error = exception;
            }
            finally
            {
                _transport.Output.Complete();
                this.StartClosing(error);
            }
        }

        private void RerouteMessage(Message message)
        {
            LogInformationReroutingMessage(this.Log, message, new EndPointLogValue(this.RemoteEndPoint));

            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                var (t, msg) = ((Connection, Message))state;
                t.RetryMessage(msg);
            }, (this, message));
        }

        private static EndPoint NormalizeEndpoint(EndPoint endpoint)
        {
            if (!(endpoint is IPEndPoint ep)) return endpoint;

            // Normalize endpoints
            if (ep.Address.IsIPv4MappedToIPv6)
            {
                return new IPEndPoint(ep.Address.MapToIPv4(), ep.Port);
            }

            return ep;
        }

        /// <summary>
        /// Handles a message receive failure.
        /// </summary>
        /// <returns><see langword="true"/> if the exception should not be caught and <see langword="false"/> if it should be caught.</returns>
        private bool HandleReceiveMessageFailure(Message message, Exception exception)
        {
            LogErrorExceptionReadingMessage(this.Log, exception, message, this.RemoteEndPoint, this.LocalEndPoint);

            // If deserialization completely failed, rethrow the exception so that it can be handled at another level.
            if (message is null || exception is InvalidMessageFrameException)
            {
                // Returning false here informs the caller that the exception should not be caught.
                return false;
            }

            // The message body was not successfully decoded, but the headers were.
            MessagingInstruments.OnRejectedMessage(message);

            if (message.HasDirection)
            {
                if (message.Direction == Message.Directions.Request)
                {
                    // Send a fast fail to the caller.
                    var response = this.MessageFactory.CreateResponseMessage(message);
                    response.Result = Message.ResponseTypes.Error;
                    response.BodyObject = Response.FromException(exception);

                    // Send the error response and continue processing the next message.
                    this.Send(response);
                }
                else if (message.Direction == Message.Directions.Response)
                {
                    // If the message was a response, propagate the exception to the intended recipient.
                    message.Result = Message.ResponseTypes.Error;
                    message.BodyObject = Response.FromException(exception);
                    this.OnReceivedMessage(message);
                }
            }

            // The exception has been handled by propagating it onwards.
            return true;
        }

        private bool HandleSendMessageFailure(Message message, Exception exception)
        {
            // We get here if we failed to serialize the msg (or any other catastrophic failure).
            // Request msg fails to serialize on the sender, so we just enqueue a rejection msg.
            // Response msg fails to serialize on the responding silo, so we try to send an error response back.
            LogErrorExceptionSendingMessage(this.Log, exception, message, this.RemoteEndPoint, this.LocalEndPoint);

            if (message is null || exception is InvalidMessageFrameException)
            {
                // Returning false here informs the caller that the exception should not be caught.
                return false;
            }

            MessagingInstruments.OnFailedSentMessage(message);

            if (message.Direction == Message.Directions.Request)
            {
                var response = this.MessageFactory.CreateResponseMessage(message);
                response.Result = Message.ResponseTypes.Error;
                response.BodyObject = Response.FromException(exception);

                this.MessageCenter.DispatchLocalMessage(response);
            }
            else if (message.Direction == Message.Directions.Response && message.RetryCount < MessagingOptions.DEFAULT_MAX_MESSAGE_SEND_RETRIES)
            {
                // If we failed sending an original response, turn the response body into an error and reply with it.
                // unless we have already tried sending the response multiple times.
                message.Result = Message.ResponseTypes.Error;
                message.BodyObject = Response.FromException(exception);
                ++message.RetryCount;

                this.Send(message);
            }
            else
            {
                LogWarningDroppingMessage(
                    this.Log,
                    exception,
                    message);

                MessagingInstruments.OnDroppedSentMessage(message);
            }

            return true;
        }

        private sealed class MessageHandlerPoolPolicy : PooledObjectPolicy<MessageHandler>
        {
            public override MessageHandler Create() => new MessageHandler();

            public override bool Return(MessageHandler obj)
            {
                obj.Reset();
                return true;
            }
        }

        private sealed class MessageHandler : IThreadPoolWorkItem
        {
            private Message message;
            private Connection connection;

            public void Set(Message m, Connection c)
            {
                this.message = m;
                this.connection = c;
            }

            public void Execute()
            {
                this.connection.OnReceivedMessage(this.message);
                MessageHandlerPool.Return(this);
            }

            public void Reset()
            {
                this.message = null;
                this.connection = null;
            }
        }

        private readonly struct EndPointLogValue(EndPoint endPoint)
        {
            public override string ToString() => endPoint?.ToString() ?? "(never connected)";
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Closing connection {Connection}"
        )]
        private static partial void LogInformationClosingConnection(ILogger logger, Exception exception, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception processing incoming messages on connection {Connection}"
        )]
        private static partial void LogWarningExceptionProcessingIncomingMessages(ILogger logger, Exception exception, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception processing outgoing messages on connection {Connection}"
        )]
        private static partial void LogWarningExceptionProcessingOutgoingMessages(ILogger logger, Exception exception, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception aborting connection {Connection}"
        )]
        private static partial void LogWarningExceptionAbortingConnection(ILogger logger, Exception exception, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception terminating connection {Connection}"
        )]
        private static partial void LogWarningExceptionTerminatingConnection(ILogger logger, Exception exception, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Rerouting messages for remote endpoint {EndPoint}"
        )]
        private static partial void LogInformationReroutingMessages(ILogger logger, EndPointLogValue endPoint);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Rerouted {Count} messages for remote endpoint {EndPoint}"
        )]
        private static partial void LogInformationReroutedMessages(ILogger logger, int count, EndPointLogValue endPoint);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception while processing messages from remote endpoint {EndPoint}"
        )]
        private static partial void LogWarningExceptionProcessingMessagesFromRemote(ILogger logger, Exception exception, EndPoint endPoint);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception while processing messages to remote endpoint {EndPoint}"
        )]
        private static partial void LogWarningExceptionProcessingMessagesToRemote(ILogger logger, Exception exception, EndPoint endPoint);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Rerouting message {Message} from remote endpoint {EndPoint}"
        )]
        private static partial void LogInformationReroutingMessage(ILogger logger, Message message, EndPointLogValue endPoint);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Exception reading message {Message} from remote endpoint {Remote} to local endpoint {Local}"
        )]
        private static partial void LogErrorExceptionReadingMessage(ILogger logger, Exception exception, Message message, EndPoint remote, EndPoint local);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Exception sending message {Message} to remote endpoint {Remote} from local endpoint {Local}"
        )]
        private static partial void LogErrorExceptionSendingMessage(ILogger logger, Exception exception, Message message, EndPoint remote, EndPoint local);

        [LoggerMessage(
            EventId = (int)ErrorCode.Messaging_OutgoingMS_DroppingMessage,
            Level = LogLevel.Warning,
            Message = "Dropping message which failed during serialization: {Message}"
        )]
        private static partial void LogWarningDroppingMessage(ILogger logger, Exception exception, Message message);
    }
}
