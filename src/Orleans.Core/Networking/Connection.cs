using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Messaging;

#if NETCOREAPP
using Microsoft.Extensions.ObjectPool;
#endif

namespace Orleans.Runtime.Messaging
{
    internal abstract class Connection
    {
        private static readonly Func<ConnectionContext, Task> OnConnectedDelegate = context => OnConnectedAsync(context);
        private static readonly Action<object> OnConnectionClosedDelegate = state => ((Connection)state).OnTransportConnectionClosed();
        private static readonly UnboundedChannelOptions OutgoingMessageChannelOptions = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

#if NETCOREAPP
        private static readonly ObjectPool<MessageHandler> MessageHandlerPool = ObjectPool.Create(new MessageHandlerPoolPolicy());
#else
        private readonly WaitCallback handleMessageCallback;
#endif
        private readonly ConnectionCommon shared;
        private readonly ConnectionDelegate middleware;
        private readonly Channel<Message> outgoingMessages;
        private readonly ChannelWriter<Message> outgoingMessageWriter;
        private readonly object _closeLock = new object();
        private readonly List<Message> inflight = new List<Message>(4);
        private readonly TaskCompletionSource<int> _transportConnectionClosed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDuplexPipe _transport;
        private Task _processIncomingTask;
        private Task _processOutgoingTask;
        private Task _closeTask;

        protected Connection(
            ConnectionContext connection,
            ConnectionDelegate middleware,
            ConnectionCommon shared)
        {
#if !NETCOREAPP
            this.handleMessageCallback = obj => this.OnReceivedMessage((Message)obj);
#endif
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

        public string ConnectionId => this.Context?.ConnectionId;
        public virtual EndPoint RemoteEndPoint { get; }
        public virtual EndPoint LocalEndPoint { get; }
        protected CounterStatistic MessageReceivedCounter { get; set; }
        protected CounterStatistic MessageSentCounter { get; set; }
        protected ConnectionContext Context { get; }
        protected NetworkingTrace Log => this.shared.NetworkingTrace;
        protected MessagingTrace MessagingTrace => this.shared.MessagingTrace;
        protected abstract ConnectionDirection ConnectionDirection { get; }
        protected MessageFactory MessageFactory => this.shared.MessageFactory;
        protected abstract IMessageCenter MessageCenter { get; }

        public bool IsValid => _closeTask is null;

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

            NetworkingStatisticsGroup.OnOpenedSocket(connection.ConnectionDirection);
            return connection.RunInternal();
        }

        protected virtual async Task RunInternal()
        {
            _transport = this.Context.Transport;
            _processIncomingTask = this.ProcessIncoming();
            _processOutgoingTask = this.ProcessOutgoing();
            await Task.WhenAll(_processIncomingTask, _processOutgoingTask);
        }

        /// <summary>
        /// Called immediately prior to transporting a message.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>Whether or not to continue transporting the message.</returns>
        protected abstract bool PrepareMessageForSend(Message msg);

        protected abstract void RetryMessage(Message msg, Exception ex = null);

        public async Task CloseAsync(Exception exception)
        {
            StartClosing(exception);
            if (_closeTask is Task task && !task.IsCompleted)
            {
                await _closeTask;
            }
        }

        private void OnTransportConnectionClosed()
        {
            StartClosing(new ConnectionAbortedException("Underlying connection closed"));
            _transportConnectionClosed.SetResult(0);
        }

        private void StartClosing(Exception exception)
        {
            if (_closeTask is object)
            {
                return;
            }

            TaskCompletionSource<int> completion;
            lock (_closeLock)
            {
                if (_closeTask is object)
                {
                    return;
                }

                completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _closeTask = completion.Task;
            }


            if (this.Log.IsEnabled(LogLevel.Information))
            {
                this.Log.LogInformation(
                    exception,
                    "Closing connection {Connection}",
                    this);
            }

            _ = WrapCloseAsync(this, completion);

            // Propagate the result of the close method to the task completion source.
            static async Task WrapCloseAsync(Connection self, TaskCompletionSource<int> completion)
            {
                try
                {
                    await Task.Yield();
                    await self.FinishClosing().ConfigureAwait(false);
                    completion.SetResult(0);
                }
                catch (Exception closeException)
                {
                    completion.SetException(closeException);
                }
            }
        }

        /// <summary>
        /// Close the connection. This method should only be called by <see cref="StartClosing(Exception)"/>.
        /// </summary>
        private async Task FinishClosing()
        {
            NetworkingStatisticsGroup.OnClosedSocket(this.ConnectionDirection);

            // Signal the outgoing message processor to exit gracefully.
            this.outgoingMessageWriter.TryComplete();

            var transportFeature = Context.Features.Get<IUnderlyingTransportFeature>();
            var transport = transportFeature?.Transport ?? _transport;
            await transport.Input.CompleteAsync();
            await transport.Output.CompleteAsync();

            // Try to gracefully stop the reader/writer loops, if they are running.
            try
            {
                if (_processIncomingTask is Task task && !task.IsCompleted)
                {
                    await task.ConfigureAwait(false);
                }
            }
            catch (Exception processIncomingException)
            {
                // Swallow any exceptions here.
                this.Log.LogWarning(processIncomingException, "Exception processing incoming messages on connection {Connection}", this);
            }

            try
            {
                if (_processOutgoingTask is Task task && !task.IsCompleted)
                {
                    await task.ConfigureAwait(false);
                }
            }
            catch (Exception processOutgoingException)
            {
                // Swallow any exceptions here.
                this.Log.LogWarning(processOutgoingException, "Exception processing outgoing messages on connection {Connection}", this);
            }

            // Wait for the transport to signal that it's closed before disposing it.
            await _transportConnectionClosed.Task;

            try
            {
                await this.Context.DisposeAsync();
            }
            catch (Exception abortException)
            {
                // Swallow any exceptions here.
                this.Log.LogWarning(abortException, "Exception terminating connection {Connection}", this);
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
                    if (this.Log.IsEnabled(LogLevel.Information))
                    {
                        this.Log.LogInformation(
                            "Rerouting messages for remote endpoint {EndPoint}",
                            this.RemoteEndPoint?.ToString() ?? "(never connected)");
                    }
                }

                ++i;
                this.RetryMessage(message);
            }

            if (i > 0 && this.Log.IsEnabled(LogLevel.Information))
            {
                this.Log.LogInformation(
                    "Rerouted {Count} messages for remote endpoint {EndPoint}",
                    i,
                    this.RemoteEndPoint?.ToString() ?? "(never connected)");
            }
        }

        public virtual void Send(Message message)
        {
            if (!this.outgoingMessageWriter.TryWrite(message))
            {
                this.RerouteMessage(message);
            }
        }

        public override string ToString() => $"[Local: {this.LocalEndPoint}, Remote: {this.RemoteEndPoint}, ConnectionId: {this.Context.ConnectionId}]";

        protected abstract void OnReceivedMessage(Message message);

        protected abstract void OnSendMessageFailure(Message message, string error);

        private async Task ProcessIncoming()
        {
            await Task.Yield();

            Exception error = default;
            var serializer = this.shared.ServiceProvider.GetRequiredService<IMessageSerializer>();
            try
            {
                var input = this._transport.Input;
                var requiredBytes = 0;
                Message message = default;
                while (true)
                {
                    var readResult = await input.ReadAsync();

                    var buffer = readResult.Buffer;
                    if (buffer.Length >= requiredBytes)
                    {
                        do
                        {
                            try
                            {
                                int headerLength, bodyLength;
                                (requiredBytes, headerLength, bodyLength) = serializer.TryRead(ref buffer, out message);
                                if (requiredBytes == 0)
                                {
                                    MessagingStatisticsGroup.OnMessageReceive(this.MessageReceivedCounter, message, bodyLength + headerLength, headerLength, this.ConnectionDirection);
#if NETCOREAPP
                                    var handler = MessageHandlerPool.Get();
                                    handler.Set(message, this);
                                    ThreadPool.UnsafeQueueUserWorkItem(handler, preferLocal: true);
#else
                                    ThreadPool.UnsafeQueueUserWorkItem(this.handleMessageCallback, message);
#endif
                                    message = null;
                                }
                            }
                            catch (Exception exception) when (this.HandleReceiveMessageFailure(message, exception))
                            {
                            }
                        } while (requiredBytes == 0);
                    }

                    if (readResult.IsCanceled || readResult.IsCompleted) break;
                    input.AdvanceTo(buffer.Start, buffer.End);
                }
            }
            catch (Exception exception)
            {
                if (IsValid)
                {
                    this.Log.LogWarning(
                        exception,
                        "Exception while processing messages from remote endpoint {EndPoint}",
                        this.RemoteEndPoint);
                }

                error = exception;
            }
            finally
            {
                this.StartClosing(error);
            }
        }

        private async Task ProcessOutgoing()
        {
            await Task.Yield();

            Exception error = default;   
            var serializer = this.shared.ServiceProvider.GetRequiredService<IMessageSerializer>();
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
                            var (headerLength, bodyLength) = serializer.Write(ref output, message);
                            MessagingStatisticsGroup.OnMessageSend(this.MessageSentCounter, message, headerLength + bodyLength, headerLength, this.ConnectionDirection);
                        }
                    }
                    catch (Exception exception) when (message != default)
                    {
                        this.OnMessageSerializationFailure(message, exception);
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
                    this.Log.LogWarning(
                        exception,
                        "Exception while processing messages to remote endpoint {EndPoint}",
                        this.RemoteEndPoint);
                }

                error = exception;
            }
            finally
            {
                this.StartClosing(error);
            }
        }

        private void RerouteMessage(Message message)
        {
            if (this.Log.IsEnabled(LogLevel.Information))
            {
                this.Log.LogInformation(
                    "Rerouting message {Message} from remote endpoint {EndPoint}",
                    message,
                    this.RemoteEndPoint?.ToString() ?? "(never connected)");
            }

            ThreadPool.UnsafeQueueUserWorkItem(
                msg => this.RetryMessage((Message)msg),
                message);
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
            this.Log.LogWarning(
                exception,
                "Exception reading message {Message} from remote endpoint {Remote} to local endpoint {Local}",
                message,
                this.RemoteEndPoint,
                this.LocalEndPoint);

            // If deserialization completely failed, rethrow the exception so that it can be handled at another level.
            if (message?.Headers is null)
            {
                // Returning false here informs the caller that the exception should not be caught.
                return false;
            }

            // The message body was not successfully decoded, but the headers were.
            MessagingStatisticsGroup.OnRejectedMessage(message);

            if (message.Direction == Message.Directions.Request)
            {
                // Send a fast fail to the caller.
                var response = this.MessageFactory.CreateResponseMessage(message);
                response.Result = Message.ResponseTypes.Error;
                response.BodyObject = Response.ExceptionResponse(exception);

                // Send the error response and continue processing the next message.
                this.Send(response);
            }
            else if (message.Direction == Message.Directions.Response)
            {
                // If the message was a response, propagate the exception to the intended recipient.
                message.Result = Message.ResponseTypes.Error;
                message.BodyObject = Response.ExceptionResponse(exception);
                this.MessageCenter.OnReceivedMessage(message);
            }

            // The exception has been handled by propagating it onwards.
            return true;
        }

        private void OnMessageSerializationFailure(Message message, Exception exception)
        {
            // we only get here if we failed to serialize the msg (or any other catastrophic failure).
            // Request msg fails to serialize on the sender, so we just enqueue a rejection msg.
            // Response msg fails to serialize on the responding silo, so we try to send an error response back.
            this.Log.LogWarning(
                (int)ErrorCode.Messaging_SerializationError,
                exception,
                "Unexpected error serializing message {Message}",
                message);

            MessagingStatisticsGroup.OnFailedSentMessage(message);

            if (message.Direction == Message.Directions.Request)
            {
                var response = this.MessageFactory.CreateResponseMessage(message);
                response.Result = Message.ResponseTypes.Error;
                response.BodyObject = Response.ExceptionResponse(exception);

                this.MessageCenter.OnReceivedMessage(response);
            }
            else if (message.Direction == Message.Directions.Response && message.RetryCount < MessagingOptions.DEFAULT_MAX_MESSAGE_SEND_RETRIES)
            {
                // If we failed sending an original response, turn the response body into an error and reply with it.
                // unless we have already tried sending the response multiple times.
                message.Result = Message.ResponseTypes.Error;
                message.BodyObject = Response.ExceptionResponse(exception);
                ++message.RetryCount;

                this.Send(message);
            }
            else
            {
                this.Log.LogWarning(
                    (int)ErrorCode.Messaging_OutgoingMS_DroppingMessage,
                    exception,
                    "Dropping message which failed during serialization: {Message}",
                    message);

                MessagingStatisticsGroup.OnDroppedSentMessage(message);
            }
        }

#if NETCOREAPP
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
#endif
    }
}
