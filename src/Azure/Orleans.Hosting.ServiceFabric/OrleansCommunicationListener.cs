using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.ServiceFabric;

namespace Orleans.Hosting.ServiceFabric
{
    /// <summary>
    /// Service Fabric communication listener which hosts an Orleans silo.
    /// </summary>
    public class OrleansCommunicationListener : ICommunicationListener
    {
        private readonly Action<SiloHostBuilder> configure;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansCommunicationListener" /> class.
        /// </summary>
        public OrleansCommunicationListener(Action<SiloHostBuilder> configure)
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
                        services.AddOptions<FabricSiloInfo>().Configure<ILocalSiloDetails>((info, details) =>
                        {
                            info.Name = details.Name;
                            info.Silo = details.SiloAddress.ToParsableString();
                            if (details.GatewayAddress != null)
                            {
                                info.Gateway = details.GatewayAddress.ToParsableString();
                            }
                        });
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
    }
}