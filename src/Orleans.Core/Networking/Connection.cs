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
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal abstract class Connection
    {
        private static readonly Func<ConnectionContext, Task> OnConnectedDelegate = context => OnConnectedAsync(context);
        private static readonly Action<object> OnConnectionClosedDelegate = state => ((Connection)state).CloseInternal(new ConnectionAbortedException("Connection closed"));
        private static readonly UnboundedChannelOptions OutgoingMessageChannelOptions = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

        private readonly ConnectionDelegate middleware;
        private readonly IServiceProvider serviceProvider;
        private readonly Channel<Message> outgoingMessages;
        private readonly ChannelWriter<Message> outgoingMessageWriter;
        private readonly object lockObj = new object();
        private readonly List<Message> inflight = new List<Message>(4);
        private CancellationTokenRegistration closeRegistration;

        protected Connection(
            ConnectionContext connection,
            ConnectionDelegate middleware,
            IServiceProvider serviceProvider,
            INetworkingTrace trace)
        {
            this.Context = connection ?? throw new ArgumentNullException(nameof(connection));
            this.middleware = middleware ?? throw new ArgumentNullException(nameof(middleware));
            this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            this.Log = trace ?? throw new ArgumentNullException(nameof(trace));
            this.outgoingMessages = Channel.CreateUnbounded<Message>(OutgoingMessageChannelOptions);
            this.outgoingMessageWriter = this.outgoingMessages.Writer;

            // Set the connection on the connection context so that it can be retrieved by the middleware.
            this.Context.Features.Set<Connection>(this);

            this.RemoteEndPoint = NormalizeEndpoint(this.Context.RemoteEndPoint);
            this.LocalEndPoint = NormalizeEndpoint(this.Context.LocalEndPoint);
            this.IsValid = true;
        }

        public ConnectionContext Context { get; }
        protected INetworkingTrace Log { get; }
        protected abstract IMessageCenter MessageCenter { get; }
        public virtual EndPoint RemoteEndPoint { get; }
        public virtual EndPoint LocalEndPoint { get; }
        protected CounterStatistic MessageReceivedCounter { get; set; }
        protected CounterStatistic MessageSentCounter { get; set; }
        protected abstract ConnectionDirection ConnectionDirection { get; }

        public bool IsValid { get; private set; }

        public static void ConfigureBuilder(ConnectionBuilder builder) => builder.Run(OnConnectedDelegate);

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
                this.CloseInternal(error);
                this.RerouteMessages().Ignore();
                await this.Context.DisposeAsync();
            }
        }

        private static Task OnConnectedAsync(ConnectionContext context)
        {
            var connection = context.Features.Get<Connection>();

            lock (connection.lockObj)
            {
                connection.closeRegistration = context.ConnectionClosed.Register(OnConnectionClosedDelegate, connection);
            }

            return connection.RunInternal();
        }

        protected virtual Task RunInternal() => Task.WhenAll(this.ProcessIncoming(), this.ProcessOutgoing());

        public void Close(ConnectionAbortedException exception = default) => this.CloseInternal(exception);

        /// <summary>
        /// Called immediately prior to transporting a message.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>Whether or not to continue transporting the message.</returns>
        protected abstract bool PrepareMessageForSend(Message msg);

        protected abstract void OnMessageSerializationFailure(Message msg, Exception exc);

        protected abstract void RetryMessage(Message msg, Exception ex = null);

        private void CloseInternal(Exception exception)
        {
            if (!this.IsValid) return;

            lock (this.lockObj)
            {
                try
                {
                    if (!this.IsValid) return;
                    this.IsValid = false;

                    this.closeRegistration.Dispose();
                    this.closeRegistration = default;
                    if (this.Log.IsEnabled(LogLevel.Information))
                    {
                        this.Log.LogInformation(
                            "Closing connection with remote endpoint {EndPoint}",
                            this.RemoteEndPoint,
                            Environment.StackTrace);
                    }

                    // Try to gracefully stop the reader/writer loops.
                    this.Context.Transport.Input.CancelPendingRead();
                    this.Context.Transport.Output.CancelPendingFlush();
                    this.outgoingMessageWriter.TryComplete();

                    if (exception is null)
                    {
                        this.Context.Abort();
                    }
                    else
                    {
                        var abortedException = exception as ConnectionAbortedException
                            ?? new ConnectionAbortedException(
                                    $"Connection closed. See {nameof(Exception.InnerException)}",
                                    exception);

                        this.Context.Abort(abortedException);
                    }
                }
                catch (Exception innerException)
                {
                    // Swallow any exceptions here.
                    this.Log.LogWarning(innerException, "Exception closing connection with remote endpoint {EndPoint}: {Exception}", this.RemoteEndPoint, innerException);
                }
            }
        }

        public void Send(Message message)
        {
            if (!this.outgoingMessageWriter.TryWrite(message))
            {
                this.RerouteMessage(message);
            }
        }

        public override string ToString() => $"Local: {this.LocalEndPoint}, Remote: {this.RemoteEndPoint}, ConnectionId: {this.Context.ConnectionId}";

        protected abstract void OnReceivedMessage(Message message);

        protected abstract void OnReceiveMessageFailure(Message message, Exception exception);

        protected abstract void OnSendMessageFailure(Message message, string error);

        private async Task ProcessIncoming()
        {
            await Task.Yield();

            Exception error = default;
            PipeReader input = default;
            var serializer = this.serviceProvider.GetRequiredService<IMessageSerializer>();
            try
            {
                if (this.Log.IsEnabled(LogLevel.Information))
                {
                    this.Log.LogInformation(
                        "Starting to process messages from remote endpoint {RemoteEndPoint} to local endpoint {LocalEndPoint}",
                        this.RemoteEndPoint,
                        this.LocalEndPoint);
                }

                input = this.Context.Transport.Input;
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
                                    this.OnReceivedMessage(message);
                                    message = null;
                                }
                            }
                            catch (Exception exception)
                            {
                                this.Log.LogWarning(
                                    "Exception reading message {Message} from remote endpoint {RemoteEndPoint} to local endpoint {LocalEndPoint}: {Exception}",
                                    message,
                                    this.RemoteEndPoint,
                                    this.LocalEndPoint,
                                    exception);

                                this.OnReceiveMessageFailure(message, exception);
                                break;
                            }
                        } while (requiredBytes == 0);
                    }

                    if (readResult.IsCanceled || readResult.IsCompleted) break;
                    input.AdvanceTo(buffer.Start, buffer.End);
                }
            }
            catch (Exception exception)
            {
                this.Log.LogWarning(
                    exception,
                    "Exception while processing messages from remote endpoint {EndPoint}: {Exception}",
                    this.RemoteEndPoint,
                    exception);
                error = exception;
            }
            finally
            {
                input?.Complete();

                if (this.Log.IsEnabled(LogLevel.Information))
                {
                    this.Log.LogInformation(
                        "Completed processing messages from remote endpoint {EndPoint}",
                        this.RemoteEndPoint);
                }

                this.CloseInternal(error);
            }
        }

        private async Task ProcessOutgoing()
        {
            await Task.Yield();

            Exception error = default;   
            PipeWriter output = default;
            var serializer = this.serviceProvider.GetRequiredService<IMessageSerializer>();
            try
            {
                output = this.Context.Transport.Output;
                var reader = this.outgoingMessages.Reader;
                if (this.Log.IsEnabled(LogLevel.Information))
                {
                    this.Log.LogInformation(
                        "Starting to process messages from local endpoint {LocalEndPoint} to remote endpoint {RemoteEndPoint}",
                        this.LocalEndPoint,
                        this.RemoteEndPoint);
                }

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
                        this.Log.LogWarning(
                            "Exception writing message {Message} to remote endpoint {EndPoint}: {Exception}",
                            message,
                            this.RemoteEndPoint,
                            exception);
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
                this.Log.LogWarning(
                    exception,
                    "Exception while processing messages to remote endpoint {EndPoint}: {Exception}",
                    this.RemoteEndPoint,
                    exception);
                error = exception;
            }
            finally
            {
                output?.Complete();

                if (this.Log.IsEnabled(LogLevel.Information))
                {
                    this.Log.LogInformation(
                        "Completed processing messages to remote endpoint {EndPoint}",
                        this.RemoteEndPoint);
                }

                this.CloseInternal(error);
            }
        }

        private async Task RerouteMessages()
        {
            lock (this.lockObj)
            {
                foreach (var message in this.inflight)
                {
                    this.OnSendMessageFailure(message, "Connection terminated");
                }

                this.inflight.Clear();
            }

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

                    // Wait some time before re-sending the first time around.
                    await Task.Delay(TimeSpan.FromSeconds(2));
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
    }
}
