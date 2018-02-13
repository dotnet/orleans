using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Orleans.Hosting;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Validates basic <see cref="EndpointOptions"/> configuration.
    /// </summary>
    internal class EndpointOptionsValidator : IConfigurationValidator
    {
        private readonly IServiceProvider serviceProvider;

        public EndpointOptionsValidator(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            var options = this.serviceProvider.GetService<IOptions<EndpointOptions>>().Value;

            if (options.AdvertisedIPAddress == null)
            {
                throw new OrleansConfigurationException(
                    $"No listening address specified. Use {nameof(ISiloHostBuilder)}.{nameof(EndpointOptionsExtensions.ConfigureEndpoints)}(...) "
                    + $"to configure endpoints and ensure that {nameof(options.AdvertisedIPAddress)} is set.");
            }

            if (options.SiloPort == 0)
            {
                throw new OrleansConfigurationException(
                    $"No listening port specified. Use {nameof(ISiloHostBuilder)}.{nameof(EndpointOptionsExtensions.ConfigureEndpoints)}(...) "
                    + $"to configure endpoints and ensure that {nameof(options.SiloPort)} is set.");
            }
        }
    }
}