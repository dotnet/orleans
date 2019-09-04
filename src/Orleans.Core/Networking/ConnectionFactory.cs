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
    {
        private readonly IConnectionFactory connectionFactory;
        private readonly IServiceProvider serviceProvider;
        private ConnectionDelegate connectionDelegate;

        protected ConnectionFactory(
            IConnectionFactory connectionFactory,
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions)
        {
            this.connectionFactory = connectionFactory;
            this.serviceProvider = serviceProvider;
            this.ConnectionOptions = connectionOptions.Value;
        }

        protected ConnectionOptions ConnectionOptions { get; }

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
                    this.ConnectionOptions.ConfigureConnectionBuilder(connectionBuilder);
                    Connection.ConfigureBuilder(connectionBuilder);
                    return this.connectionDelegate = connectionBuilder.Build();
                }
            }
        }

        protected abstract Connection CreateConnection(SiloAddress address, ConnectionContext context);

        public virtual async ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
        {
            var connectionContext = await this.connectionFactory.ConnectAsync(address.Endpoint, cancellationToken);
            var connection = this.CreateConnection(address, connectionContext);
            return connection;
        }
    }
}
