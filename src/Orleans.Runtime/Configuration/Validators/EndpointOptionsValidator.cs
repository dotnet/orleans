using System;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Hosting;

namespace Orleans.Configuration
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
            var options = this.serviceProvider.GetRequiredService<IOptions<EndpointOptions>>().Value;

            if (options.SiloPort == 0)
            {
                throw new OrleansConfigurationException(
                    $"No listening port specified. Use {nameof(ISiloBuilder)}.{nameof(EndpointOptionsExtensions.ConfigureEndpoints)}(...) "
                    + $"to configure endpoints and ensure that {nameof(options.SiloPort)} is set.");
            }
        }
    }
}