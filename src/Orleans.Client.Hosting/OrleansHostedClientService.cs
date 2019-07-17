using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Client.Hosting
{

    public class OrleansHostedClientService : IHostedService
    {
        private readonly ILogger<OrleansHostedClientService> logger;
        private OrleansHostedClientStore clientStore;
        private OrleansHostedConection connection;
        private NamedOrleansHostedClientBuilder namedClientBuilder;

        public OrleansHostedClientService(
            ILogger<OrleansHostedClientService> logger,
            OrleansHostedClientStore clientStore,
            OrleansHostedConection connection,
            NamedOrleansHostedClientBuilder namedClientBuilder)
        {
            this.connection = connection;
            this.logger = logger;
            this.clientStore = clientStore;
            this.namedClientBuilder = namedClientBuilder;
           
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            this.clientStore.SetClient(
                namedClientBuilder.Name,
                namedClientBuilder.ClientBuilder.Build());

            await connection.ConnectAsync(
                this.clientStore.GetClient(
                this.namedClientBuilder.Name),
                cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await this.connection.DisconnectAsync();
        }
    }
}
