using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;

namespace Orleans.Configuration
{
    internal partial class EndpointOptionsProvider : IPostConfigureOptions<EndpointOptions>
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
                        LogWarningUnableToFindSuitableCandidate(logger, nameof(EndpointOptions), nameof(options.AdvertisedIPAddress), nameof(IPAddress.Loopback), advertisedIPAddress);
                    }
                    else
                    {
                        advertisedIPAddress = resolvedIP;
                    }
                }
                catch (Exception ex)
                {
                    LogErrorUnableToFindSuitableCandidate(logger, ex, nameof(EndpointOptions), nameof(options.AdvertisedIPAddress), nameof(IPAddress.Loopback), advertisedIPAddress);
                }

                options.AdvertisedIPAddress = advertisedIPAddress;
            }
        }


        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Unable to find a suitable candidate for {OptionName}.{PropertyName}. Falling back to {Fallback} ({AdvertisedIPAddress})"
        )]
        private static partial void LogWarningUnableToFindSuitableCandidate(ILogger logger, string optionName, string propertyName, string fallback, IPAddress advertisedIPAddress);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Unable to find a suitable candidate for {OptionName}.{PropertyName}. Falling back to {Fallback} ({AdvertisedIPAddress})"
        )]
        private static partial void LogErrorUnableToFindSuitableCandidate(ILogger logger, Exception exception, string optionName, string propertyName, string fallback, IPAddress advertisedIPAddress);
    }
}
