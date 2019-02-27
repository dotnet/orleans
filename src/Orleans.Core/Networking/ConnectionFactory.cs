using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Networking.Shared;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionFactory
#if NETSTANDARD2_1
        : IAsyncDisposable
#endif
    {
        private readonly IConnectionFactory connectionFactory;
        private readonly IServiceProvider serviceProvider;
        private readonly ConnectionOptions connectionOptions;
        private ConnectionDelegate connectionDelegate;

        protected ConnectionFactory(
            IConnectionFactory connectionFactory,
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions)
        {
            this.connectionFactory = connectionFactory;
            this.serviceProvider = serviceProvider;
            this.connectionOptions = connectionOptions.Value;
        }

        protected ConnectionDelegate ConnectionDelegate
        {
            get
            {
                if (this.connectionDelegate != null) return this.connectionDelegate;

                lock (this)
                {
                    if (this.connectionDelegate != null) return this.connectionDelegate;

                    // Configure the connection builder using the user-defined options.
                    var connectionBuilder = new ConnectionBuilder(this.serviceProvider);
                    this.connectionOptions.ConfigureConnectionBuilder(connectionBuilder);
                    Connection.ConfigureBuilder(connectionBuilder);
                    return this.connectionDelegate = connectionBuilder.Build();
                }
            }
        }

        protected abstract Connection CreateConnection(ConnectionContext context);

        public async ValueTask<Connection> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken)
        {
            var connectionContext = await this.connectionFactory.ConnectAsync(endpoint, cancellationToken);
            var connection = this.CreateConnection(connectionContext);
            return connection;
        }
    }
}
