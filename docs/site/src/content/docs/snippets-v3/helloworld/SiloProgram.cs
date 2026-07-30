using System.Net;
using HelloWorld.Grains;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace HelloWorld;

public class SiloProgram
{
    // <silo_setup>
    static async Task<ISiloHost> StartSilo(string[] args)
    {
        var siloHostBuilder = new SiloHostBuilder()
            .UseLocalhostClustering()
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "dev";
                options.ServiceId = "HelloWorldApp";
            })
            .Configure<EndpointOptions>(
                options => options.AdvertisedIPAddress = IPAddress.Loopback)
            .ConfigureApplicationParts(
                parts => parts.AddApplicationPart(typeof(HelloGrain).Assembly).WithReferences())
            .ConfigureLogging(logging => logging.AddConsole());

        var host = siloHostBuilder.Build();
        await host.StartAsync();

        return host;
    }
    // </silo_setup>

    public static async Task Main(string[] args)
    {
        var host = await StartSilo(args);
        Console.WriteLine("Press Enter to terminate...");
        Console.ReadLine();
        await host.StopAsync();
    }
}
