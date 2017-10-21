using Orleans.Runtime.Configuration;
using System;
using System.Threading.Tasks;
using HelloWorld.Grains;
using Microsoft.Extensions.Logging;
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
            try
            {
                var host = await StartSilo();
                Console.WriteLine("Press Enter to terminate...");
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

        private static async Task<ISiloHost> StartSilo()
        {
            // define the cluster configuration
            var config = ClusterConfiguration.LocalhostPrimarySilo();
            config.AddMemoryStorageProvider();

            var builder = new SiloHostBuilder()
                .UseConfiguration(config)
                .ConfigureLogging(logging => logging.AddConsole());
            // TODO: After #3578 is released, this should be enough: builder.AddApplicationPartsFromReferences(typeof(HelloGrain).Assembly);
            builder.GetApplicationPartManager().AddApplicationPart(typeof(HelloGrain).Assembly);
            builder.GetApplicationPartManager().AddApplicationPartsFromReferences(typeof(HelloGrain).Assembly);

            var host = builder.Build();
            await host.StartAsync();
            return host;
        }
    }
}
