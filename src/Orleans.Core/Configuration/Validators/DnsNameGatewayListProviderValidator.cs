using Microsoft.Extensions.Options;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Orleans.Configuration.Validators
{
    /// <summary>
    /// Validates the configuration of the DnsNameGatewayListProvider component
    /// </summary>
    internal class DnsNameGatewayListProviderValidator : IConfigurationValidator
    {
        private DnsNameGatewayListProviderOptions options;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options"></param>
        public DnsNameGatewayListProviderValidator(IOptions<DnsNameGatewayListProviderOptions> options)
        {
            this.options = options.Value;
        }

        /// <summary>
        /// Validation function
        /// </summary>
        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.DnsName))
            {
                throw new OrleansConfigurationException($"Invalid {nameof(DnsNameGatewayListProviderOptions)} value for {nameof(options.DnsName)}.  Resolvable DNS name is required.");
            }

            if (this.options.Port <= 0)
            {
                throw new OrleansConfigurationException($"Invalid {nameof(DnsNameGatewayListProviderOptions)} value for {nameof(options.Port)} must be greater than zero.");
            }
        }

    }
}
