using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.ServiceFabric;

namespace Orleans.Hosting.ServiceFabric
{
    /// <summary>
    /// Service Fabric communication listener which hosts an Orleans silo.
    /// </summary>
    public class OrleansCommunicationListener : ICommunicationListener
    {
        private readonly Action<ISiloHostBuilder> configure;
        private readonly Func<CancellationToken, Task> onOpen;
        private readonly Func<ISiloHost, CancellationToken, Task> onOpened;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansCommunicationListener" /> class.
        /// </summary>
        public OrleansCommunicationListener(Action<ISiloHostBuilder> configure, Func<CancellationToken, Task> onOpen = null, Func<ISiloHost, CancellationToken, Task> onOpened = null)
        {
            this.configure = configure ?? throw new ArgumentNullException(nameof(configure));
            this.onOpen = onOpen ?? (_ => Task.CompletedTask);
            this.onOpened = onOpened ?? ((_, __) => Task.CompletedTask);
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
                        services.AddOptions<FabricSiloInfo>()
                            .Configure<IOptions<EndpointOptions>, ILocalSiloDetails>((info, options, details) =>
                            {
                                info.Name = details.Name;
                                info.Silo = SiloAddress.New(options.Value.GetPublicSiloEndpoint(), details.SiloAddress.Generation).ToParsableString();
                                if (details.GatewayAddress != null)
                                {
                                    info.Gateway = SiloAddress.New(options.Value.GetPublicProxyEndpoint(), 0).ToParsableString();
                                }
                            });
                    });
                this.configure(builder);

                this.Host = builder.Build();
                await this.onOpen(cancellationToken);
                await this.Host.StartAsync(cancellationToken);
            }
            catch
            {
                this.Abort();
                throw;
            }
            
            var endpoint = this.Host.Services.GetRequiredService<IOptions<FabricSiloInfo>>().Value;
            var result = JsonConvert.SerializeObject(endpoint);
            await this.onOpened(this.Host, cancellationToken);
            return result;
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