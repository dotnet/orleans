using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;

namespace Orleans.Configuration
{
    internal class EndpointOptionsProvider : IPostConfigureOptions<EndpointOptions>
    {
        private readonly ILogger<EndpointOptionsProvider> logger;

        public EndpointOptionsProvider(ILogger<EndpointOptionsProvider> logger)
        {
            this.logger = logger;
        }

        public void PostConfigure(string name, EndpointOptions options)
        {
            if (options.AdvertisedIPAddress is null)
            {
                var advertisedIPAddress = IPAddress.Loopback;

                try
                {
                    var resolvedIP = ConfigUtilities.ResolveIPAddressOrDefault(null, AddressFamily.InterNetwork);

                    if (resolvedIP is null)
                    {
                        if (logger.IsEnabled(LogLevel.Warning)) logger.LogWarning($"Unable to resolve {nameof(options.AdvertisedIPAddress)} and will set to {nameof(IPAddress.Loopback)}:{advertisedIPAddress}");
                    }
                    else
                    { 
                        advertisedIPAddress = resolvedIP;
                    }
                }
                catch (Exception ex)
                {
                    if (logger.IsEnabled(LogLevel.Error))
                    {
                        logger.LogError(ex, $"Failed to resolve {nameof(options.AdvertisedIPAddress)}  and will set to {nameof(IPAddress.Loopback)}:{advertisedIPAddress}");
                    }
                }                

                options.AdvertisedIPAddress = advertisedIPAddress;
            }
        }
    }
}
