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

        private static partial class Log
        {
            [LoggerMessage(1, LogLevel.Warning, "Unable to find a suitable candidate for {OptionName}.{AdvertisedIPAddress}. Falling back to {IPAddressLoopback} ({AdvertisedIPAddress})")]
            public static partial void UnableToFindSuitableCandidate(ILogger logger, string optionName, string advertisedIPAddress, string ipAddressLoopback);

            [LoggerMessage(2, LogLevel.Error, "Unable to find a suitable candidate for {OptionName}.{AdvertisedIPAddress}. Falling back to {IPAddressLoopback} ({AdvertisedIPAddress})")]
            public static partial void UnableToFindSuitableCandidateError(ILogger logger, Exception exception, string optionName, string advertisedIPAddress, string ipAddressLoopback);
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
                        Log.UnableToFindSuitableCandidate(logger, nameof(EndpointOptions), nameof(options.AdvertisedIPAddress), nameof(IPAddress.Loopback), advertisedIPAddress.ToString());
                    }
                    else
                    { 
                        advertisedIPAddress = resolvedIP;
                    }
                }
                catch (Exception ex)
                {
                    Log.UnableToFindSuitableCandidateError(logger, ex, nameof(EndpointOptions), nameof(options.AdvertisedIPAddress), nameof(IPAddress.Loopback), advertisedIPAddress.ToString());
                }                

                options.AdvertisedIPAddress = advertisedIPAddress;
            }
        }
    }
}
