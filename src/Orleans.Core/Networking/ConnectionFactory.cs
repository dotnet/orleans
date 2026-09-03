using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionFactory
    {
        private readonly IConnectionFactory connectionFactory;
        private readonly IServiceProvider serviceProvider;
#if NET9_0_OR_GREATER
        private readonly Lock connectionDelegateLock = new();
#else
        private readonly object connectionDelegateLock = new();
#endif
        private ConnectionDelegate? connectionDelegate;

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

                lock (this.connectionDelegateLock)
                {
                    if (this.connectionDelegate != null) return this.connectionDelegate;

                    // Configure the connection builder using the user-defined options.
                    var connectionBuilder = new ConnectionBuilder(this.serviceProvider);
                    connectionBuilder.Use(next =>
                    {
                        return context =>
                        {
                            context.Features.Set<IUnderlyingTransportFeature>(new UnderlyingConnectionTransportFeature { Transport = context.Transport });
                            return next(context);
                        };
                    });
                    this.ConfigureConnectionBuilder(connectionBuilder);
                    Connection.ConfigureBuilder(connectionBuilder);
                    return this.connectionDelegate = connectionBuilder.Build();
                }
            }
        }

        protected virtual void ConfigureConnectionBuilder(IConnectionBuilder connectionBuilder) { }

        protected abstract Connection CreateConnection(SiloAddress address, ConnectionContext context);

        public virtual async ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
        {
            var connectionContext = await this.connectionFactory.ConnectAsync(address.Endpoint, cancellationToken);
            var connection = this.CreateConnection(address, connectionContext);
            return connection;
        }
    }
}
