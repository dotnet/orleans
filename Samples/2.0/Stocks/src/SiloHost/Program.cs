using System;
using System.Net;
using System.Threading.Tasks;
using HelloWorld.Grains;
using HelloWorld.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace OrleansSiloHost
{
    public class Program
    {
        public static int Main(string[] args)
        {
            return RunMainAsync().Result;
        }

        private static async Task<int> RunMainAsync()
        {
            var configurationBuilder = new ConfigurationBuilder();

            var configuration = configurationBuilder.Build();

            try
            {
                var host = await StartSilo(configuration);
                Console.WriteLine("Press Enter to terminate...");
                var client = host.Services.GetRequiredService<IClusterClient>();

                var stockGrain = client.GetGrain<IStockGrain>("MSFT");

                var price = await stockGrain.GetPrice();

                Console.WriteLine("Price is \n{0}", price);

                Console.ReadLine();

                await host.StopAsync();

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }
        }

        private static async Task<ISiloHost> StartSilo(IConfiguration configuration)
        {
            // define the cluster configuration
            var builder = new SiloHostBuilder()
                .UseLocalhostClustering()
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "dev";
                    options.ServiceId = "StocksSampleApp";
                })
                .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(StockGrain).Assembly).WithReferences())
                .ConfigureLogging(logging => logging.AddConsole())
                .EnableDirectClient();

            var host = builder.Build();
            await host.StartAsync();
            return host;
        }
    }
}
