using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal abstract class Connection
    {
        public static readonly Func<ConnectionContext, Task> OnConnectedDelegate = context => OnConnectedAsync(context);
        public static readonly object ContextItemKey = new object();
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

        protected Connection(
            ConnectionContext connection,
            ConnectionDelegate middleware,
            IServiceProvider serviceProvider,
            INetworkingTrace trace)
        {
            this.Context = connection ?? throw new ArgumentNullException(nameof(connection));
            this.middleware = middleware;
            this.serviceProvider = serviceProvider;
            this.Log = trace;
            this.outgoingMessages = Channel.CreateUnbounded<Message>(OutgoingMessageChannelOptions);
            this.outgoingMessageWriter = this.outgoingMessages.Writer;

            // Set the connection on the connection context so that it can be retrieved by the middleware.
            this.Context.Features.Set<Connection>(this);
            this.IsValid = true;
        }

        public ConnectionContext Context { get; }
        protected INetworkingTrace Log { get; }
        protected abstract IMessageCenter MessageCenter { get; }
        public virtual EndPoint RemoteEndpoint => this.Context.RemoteEndPoint;
        public virtual EndPoint LocalEndpoint => this.Context.LocalEndPoint;
        public bool IsValid { get; private set; }

        public static void ConfigureBuilder(ConnectionBuilder builder) => builder.Run(OnConnectedDelegate);

        public static async Task OnConnectedAsync(ConnectionContext context)
        {
            var connection = context.Features.Get<Connection>();
            context.ConnectionClosed.Register(
                state => ((Connection)state).CloseInternal(new ConnectionAbortedException("Connection closed")),
                connection);
            await connection.RunInternal();
        }

        public async Task Run()
        {
            Exception error = default;
            try
            {
                await this.middleware(this.Context);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            try
            {
                if (error is ConnectionAbortedException abortedException)
                {
                    this.CloseInternal(abortedException);
                }
                else if (error != null)
                {
                    this.CloseInternal(new ConnectionAbortedException(
                        $"Connection aborted. See {nameof(Exception.InnerException)}",
                        error));
                }
                else if (error == null)
                {
                    this.CloseInternal(new ConnectionAbortedException("Connection processing completed without error"));
                }

                await this.Context.DisposeAsync();
            }
            catch
            {
                // Swallow any exceptions here.
            }

            _ = this.RerouteMessages();
        }

        protected virtual async Task RunInternal()
        {
            var outgoingTask = Task.Run(this.ProcessOutgoing);
            var incomingTask = Task.Run(this.ProcessIncoming);
            await Task.WhenAll(outgoingTask, incomingTask);
        }
        
        public void Close(ConnectionAbortedException exception = default)
        {
            if (this.Log.IsEnabled(LogLevel.Information))
            {
                this.Log.LogInformation(
                    "Closing connection with remote endpoint {EndPoint}",
                    this.RemoteEndpoint);
            }

            this.CloseInternal(exception);
        }

        /// <summary>
        /// Called immediately prior to transporting a message.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>Whether or not to continue transporting the message.</returns>
        protected abstract bool PrepareMessageForSend(Message msg);

        protected abstract void OnMessageSerializationFailure(Message msg, Exception exc);

        protected abstract void RetryMessage(Message msg, Exception ex = null);

        private void CloseInternal(ConnectionAbortedException exception)
        {
            try
            {
                if (!this.IsValid) return;

                lock (this.lockObj)
                {
                    if (!this.IsValid) return;
                    this.IsValid = false;
                }

                // Try to gracefully stop the reader/writer loops.
                this.Context.Transport.Input.CancelPendingRead();
                this.Context.Transport.Output.CancelPendingFlush();
                this.outgoingMessageWriter.TryComplete();

                if (exception == null)
                {
                    this.Context.Abort();
                }
                else
                {
                    this.Context.Abort(exception);
                }
            }
            catch
            {
            }
        }

        public void Send(Message message)
        {
            if (!this.outgoingMessageWriter.TryWrite(message))
            {
                this.RerouteMessage(message);
            }
        }

        protected abstract void OnReceivedMessage(Message message);

        protected abstract void OnReceiveMessageFailure(Message message, Exception exception);

        protected abstract void OnSendMessageFailure(Message message, string error);

        private async Task ProcessIncoming()
        {
            PipeReader input = default;
            var serializer = this.serviceProvider.GetRequiredService<IMessageSerializer>();
            try
            {
                if (this.Log.IsEnabled(LogLevel.Debug))
                {
                    this.Log.LogDebug(
                        "Starting to process messages from remote endpoint {RemoteEndPoint} to local endpoint {LocalEndPoint}",
                        this.RemoteEndpoint,
                        this.LocalEndpoint);
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
                                requiredBytes = serializer.TryRead(ref buffer, out message);
                                if (requiredBytes == 0)
                                {
                                    this.OnReceivedMessage(message);
                                    message = null;
                                }
                            }
                            catch (Exception exception)
                            {
                                this.Log.LogWarning(
                                    "Exception reading message {Message} from remote endpoint {RemoteEndPoint} to local endpoint {LocalEndPoint}: {Exception}",
                                    message,
                                    this.RemoteEndpoint,
                                    this.LocalEndpoint,
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
            catch (ConnectionAbortedException) { }
            finally
            {
                input.Complete();

                if (this.Log.IsEnabled(LogLevel.Debug))
                {
                    this.Log.LogDebug(
                        "Completed processing messages from remote endpoint {EndPoint}",
                        this.RemoteEndpoint);
                }
            }
        }

        private async Task ProcessOutgoing()
        {
            PipeWriter output = default;
            var serializer = this.serviceProvider.GetRequiredService<IMessageSerializer>();
            var connectionClosed = this.Context.ConnectionClosed;
            try
            {
                output = this.Context.Transport.Output;
                var reader = this.outgoingMessages.Reader;
                if (this.Log.IsEnabled(LogLevel.Debug))
                {
                    this.Log.LogDebug(
                        "Starting to process messages from local endpoint {LocalEndPoint} to remote endpoint {RemoteEndPoint}",
                        this.LocalEndpoint,
                        this.RemoteEndpoint);
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
                            serializer.Write(ref output, message);
                        }
                    }
                    catch (Exception exception) when (message != default)
                    {
                        this.Log.LogWarning(
                            "Exception writing message {Message} to remote endpoint {EndPoint}: {Exception}",
                            message,
                            this.RemoteEndpoint,
                            exception);
                        this.OnMessageSerializationFailure(message, exception);
                    }

                    var flushResult = await output.FlushAsync();
                    if (flushResult.IsCompleted || flushResult.IsCanceled || connectionClosed.IsCancellationRequested)
                    {
                        break;
                    }

                    inflight.Clear();
                }
            }
            catch (ConnectionAbortedException) { }
            finally
            {
                output.Complete();

                if (this.Log.IsEnabled(LogLevel.Debug))
                {
                    this.Log.LogDebug(
                        "Completed processing messages to remote endpoint {EndPoint}",
                        this.RemoteEndpoint);
                }
            }
        }

        private async Task RerouteMessages()
        {
            var i = 0;
            foreach (var message in this.inflight)
            {
                this.OnSendMessageFailure(message, "Connection terminated");
            }

            this.inflight.Clear();

            while (this.outgoingMessages.Reader.TryRead(out var message))
            {
                if (i == 0)
                {
                    if (this.Log.IsEnabled(LogLevel.Information))
                    {
                        this.Log.LogInformation(
                            "Rerouting messages from remote endpoint {EndPoint}",
                            this.RemoteEndpoint?.ToString() ?? "(never connected)");
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
                    "Rerouted {Count} messages from remote endpoint {EndPoint}",
                    i,
                    this.RemoteEndpoint?.ToString() ?? "(never connected)");
            }
        }

        private void RerouteMessage(Message message)
        {
            if (this.Log.IsEnabled(LogLevel.Debug))
            {
                this.Log.LogDebug(
                    "Rerouting message {Message} from remote endpoint {EndPoint}",
                    message,
                    this.RemoteEndpoint?.ToString() ?? "(never connected)");
            }

            ThreadPool.UnsafeQueueUserWorkItem(
                msg => this.RetryMessage((Message)msg),
                message);
        }
    }
}
