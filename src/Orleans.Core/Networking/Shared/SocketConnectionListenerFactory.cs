using System;
using System.Net;
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

        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            if (!(endpoint is IPEndPoint ipEndpoint))
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            var listener = new SocketConnectionListener(ipEndpoint, this.socketConnectionOptions, this.trace, this.schedulers);
            listener.Bind();
            return new ValueTask<IConnectionListener>(listener);
        }
    }
}
