using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Networking.Shared
{
    internal class SocketConnectionFactory : IConnectionFactory
    {
        private readonly SocketsTrace trace;
        private readonly SocketSchedulers schedulers;
        private readonly MemoryPool<byte> memoryPool;

        public SocketConnectionFactory(ILoggerFactory loggerFactory, SocketSchedulers schedulers, SharedMemoryPool memoryPool)
        {
            var logger = loggerFactory.CreateLogger("Orleans.Sockets");
            this.trace = new SocketsTrace(logger);
            this.schedulers = schedulers;
            this.memoryPool = memoryPool.Pool;
        }

        public async ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken)
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                LingerState = new LingerOption(true, 0),
                NoDelay = true
            };

            socket.EnableFastPath();
            var completion = new SingleUseSocketAsyncEventArgs
            {
                RemoteEndPoint = endpoint
            };
            
            if (!socket.ConnectAsync(completion))
            {
                completion.Complete();
            }

            await completion;

            if (completion.SocketError != SocketError.Success)
            {
                throw new SocketConnectionException($"Unable to connect to {endpoint}. Error: {completion.SocketError}");
            }

            var scheduler = this.schedulers.GetScheduler();
            var connection = new SocketConnection(socket, this.memoryPool, scheduler, this.trace);
            connection.Start();
            return connection;
        }

        public class SingleUseSocketAsyncEventArgs : SocketAsyncEventArgs, ICriticalNotifyCompletion
        {
            private readonly TaskCompletionSource<SingleUseSocketAsyncEventArgs> completion
                = new TaskCompletionSource<SingleUseSocketAsyncEventArgs>();

            public TaskAwaiter<SingleUseSocketAsyncEventArgs> GetAwaiter() => this.completion.Task.GetAwaiter();

            public void Complete() => this.completion.TrySetResult(this);

            public void OnCompleted(Action continuation) => this.GetAwaiter().OnCompleted(continuation);

            public void UnsafeOnCompleted(Action continuation) => this.GetAwaiter().UnsafeOnCompleted(continuation);

            protected override void OnCompleted(SocketAsyncEventArgs _) => this.completion.TrySetResult(this);
        }
    }

    [Serializable]
    public sealed class SocketConnectionException : OrleansException
    {
        public SocketConnectionException(string message) : base(message) { }

        public SocketConnectionException(string message, Exception innerException) : base(message, innerException) { }

        public SocketConnectionException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}