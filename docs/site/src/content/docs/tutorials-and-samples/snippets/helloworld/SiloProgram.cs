using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace HelloWorld;

public class SiloProgram
{
    // <silo_setup>
    public static async Task SiloMain(string[] args)
    {
        await Host.CreateDefaultBuilder(args)
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering()
                    .Configure<ClusterOptions>(options =>
                    {
                        options.ClusterId = "dev";
                        options.ServiceId = "HelloWorldApp";
                    })
                    .Configure<EndpointOptions>(
                        options => options.AdvertisedIPAddress = IPAddress.Loopback)
                    .ConfigureLogging(logging => logging.AddConsole());
            })
            .RunConsoleAsync();
    }
    // </silo_setup>
}
