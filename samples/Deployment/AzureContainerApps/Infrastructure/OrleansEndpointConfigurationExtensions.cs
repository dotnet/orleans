using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;

namespace Infrastructure;

public static class OrleansEndpointConfigurationExtensions
{
    public static ISiloBuilder ConfigureSampleEndpoints(
        this ISiloBuilder siloBuilder,
        IConfiguration configuration,
        IHostEnvironment environment,
        int developmentSiloPort,
        int developmentGatewayPort)
    {
        var advertisedIpValue = configuration["Orleans:AdvertisedIPAddress"];
        if (environment.IsDevelopment() && string.IsNullOrWhiteSpace(advertisedIpValue))
        {
            return siloBuilder.ConfigureEndpoints(developmentSiloPort, developmentGatewayPort);
        }

        if (!IPAddress.TryParse(advertisedIpValue, out var advertisedIp))
        {
            throw new InvalidOperationException(
                "Orleans:AdvertisedIPAddress must contain the Container Apps environment private IP.");
        }

        var advertisedSiloPort = GetRequiredPort(configuration, "Orleans:AdvertisedSiloPort");
        var advertisedGatewayPort = GetRequiredPort(configuration, "Orleans:AdvertisedGatewayPort");

        return siloBuilder.Configure<EndpointOptions>(options =>
        {
            options.AdvertisedIPAddress = advertisedIp;
            options.SiloPort = advertisedSiloPort;
            options.GatewayPort = advertisedGatewayPort;
            options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, 11_111);
            options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, 30_000);
        });
    }

    private static int GetRequiredPort(IConfiguration configuration, string key)
    {
        var value = AzureTableServiceClientFactory.GetRequiredValue(configuration, key);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > IPEndPoint.MaxPort)
        {
            throw new InvalidOperationException($"{key} must be a valid TCP port.");
        }

        return port;
    }
}
