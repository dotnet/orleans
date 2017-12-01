using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceFabric;

namespace Orleans.Hosting.ServiceFabric
{
    /// <summary>
    /// Service Fabric communication listener which hosts an Orleans silo.
    /// </summary>
    public class OrleansCommunicationListener : ICommunicationListener
    {
        private readonly Action<ISiloHostBuilder> configure;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansCommunicationListener" /> class.
        /// </summary>
        public OrleansCommunicationListener(Action<ISiloHostBuilder> configure)
        {
            this.configure = configure ?? throw new ArgumentNullException(nameof(configure));
        }

        /// <summary>
        /// Gets or sets the underlying <see cref="ISiloHost"/>.
        /// </summary>
        /// <remarks>Only valid after <see cref="OpenAsync"/> has been invoked. Exposed for testability.</remarks>
        public ISiloHost Host { get; private set; }

        /// <inheritdoc />
        public async Task<string> OpenAsync(CancellationToken cancellationToken)
        {
            try
            {
                var builder = new SiloHostBuilder();
                builder.ConfigureServices(
                    services =>
                    {
                        services.AddSingleton<IConfigureOptions<FabricSiloInfo>, SiloInfoConfiguration>();
                    });
                this.configure(builder);

                this.Host = builder.Build();
                await this.Host.StartAsync(cancellationToken);
            }
            catch
            {
                this.Abort();
                throw;
            }
            
            var endpoint = this.Host.Services.GetRequiredService<IOptions<FabricSiloInfo>>().Value;
            return JsonConvert.SerializeObject(endpoint);
        }

        /// <inheritdoc />
        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            var siloHost = this.Host;
            if (siloHost != null)
            {
                await siloHost.StopAsync(cancellationToken);
            }

            this.Host = null;
        }

        /// <inheritdoc />
        public void Abort()
        {
            var host = this.Host;
            if (host == null) return;

            var cancellation = new CancellationTokenSource();
            cancellation.Cancel(false);

            try
            {
                host.StopAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore.
            }
            finally
            {
                this.Host = null;
            }
        }

        // ReSharper disable once ClassNeverInstantiated.Local
        private class SiloInfoConfiguration : IConfigureOptions<FabricSiloInfo>
        {
            private readonly NodeConfiguration config;

            public SiloInfoConfiguration(NodeConfiguration config)
            {
                this.config = config;
            }

            public void Configure(FabricSiloInfo info)
            {
                info.Name = this.config.SiloName;
                info.Silo = SiloAddress.New(this.config.Endpoint, this.config.Generation).ToParsableString();
                if (this.config.ProxyGatewayEndpoint != null)
                {
                    info.Gateway = SiloAddress.New(this.config.ProxyGatewayEndpoint, this.config.Generation).ToParsableString();
                }
            }
        }
    }
}