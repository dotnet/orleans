using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace LocalDev;

public class LocalDevelopment
{
    // <silo_localhost>
    public static async Task StartLocalSilo()
    {
        try
        {
            var host = await BuildAndStartSiloAsync();

            Console.WriteLine("Press Enter to terminate...");
            Console.ReadLine();

            await host.StopAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    static async Task<IHost> BuildAndStartSiloAsync()
    {
        var host = new HostBuilder()
          .UseOrleans(builder =>
          {
              builder.UseLocalhostClustering()
                  .Configure<ClusterOptions>(options =>
                  {
                      options.ClusterId = "dev";
                      options.ServiceId = "MyAwesomeService";
                  })
                  .Configure<EndpointOptions>(
                      options => options.AdvertisedIPAddress = IPAddress.Loopback)
                  .ConfigureLogging(logging => logging.AddConsole());
          })
          .Build();

        await host.StartAsync();

        return host;
    }
    // </silo_localhost>

    // <client_localhost>
    public static async Task StartLocalClient()
    {
        var client = new ClientBuilder()
            .UseLocalhostClustering()
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "dev";
                options.ServiceId = "MyAwesomeService";
            })
            .ConfigureLogging(logging => logging.AddConsole())
            .Build();

        await client.Connect();
    }
    // </client_localhost>
}
