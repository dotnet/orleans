using System;
using System.Net;
using System.Threading.Tasks;
using HelloWorld.Grains;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace OrleansSiloHost
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                var host = new HostBuilder()
                    .UseOrleans((context, siloBuilder) =>
                    {
                        siloBuilder
                            .UseLocalhostClustering()
                            .Configure<ClusterOptions>(options =>
                            {
                                options.ClusterId = "dev";
                                options.ServiceId = "HelloWorldApp";
                            })
                            .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                            .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(HelloGrain).Assembly).WithReferences());
                    })
                    .ConfigureLogging(logging => logging.AddConsole())
                    .Build();
                await host.RunAsync();

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }
        }
    }
}
