using System;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Networking.Shared
{
    internal sealed class SocketConnectionListenerFactory : IConnectionListenerFactory
    {
        private readonly SocketConnectionOptions socketConnectionOptions;
        private readonly SocketsTrace trace;
        private readonly SocketSchedulers schedulers;

        public SocketConnectionListenerFactory(
            ILoggerFactory loggerFactory,
            IOptions<SocketConnectionOptions> socketConnectionOptions,
            SocketSchedulers schedulers)
        {
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            this.socketConnectionOptions = socketConnectionOptions.Value;
            var logger = loggerFactory.CreateLogger("Orleans.Sockets");
            this.trace = new SocketsTrace(logger);
            this.schedulers = schedulers;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "Successful binding transfers ownership to the caller; failed binding transfers the listener to DisposeAndThrowAsync.")]
        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            if (endpoint is not IPEndPoint ipEndpoint)
            {
                throw new ArgumentException($"The endpoint must be an {nameof(IPEndPoint)}.", nameof(endpoint));
            }

            var listener = new SocketConnectionListener(ipEndpoint, this.socketConnectionOptions, this.trace, this.schedulers);
            try
            {
                listener.Bind();
                return new(listener);
            }
            catch (Exception exception)
            {
                return DisposeAndThrowAsync(listener, exception);
            }
        }

        private static async ValueTask<IConnectionListener> DisposeAndThrowAsync(SocketConnectionListener listener, Exception exception)
        {
            await listener.DisposeAsync();
            ExceptionDispatchInfo.Throw(exception);
            return null!;
        }
    }
}
