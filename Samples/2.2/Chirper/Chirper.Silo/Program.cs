using System;
using Chirper.Grains;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

namespace Chirper.Silo
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = nameof(Silo);

            var host = new SiloHostBuilder()
                .UseLocalhostClustering()
                .ConfigureApplicationParts(_ => _.AddApplicationPart(typeof(ChirperAccount).Assembly).WithReferences())
                .ConfigureLogging(_ =>
                {
                    _.AddFilter("Orleans.Runtime.Management.ManagementGrain", LogLevel.Warning);
                    _.AddFilter("Orleans.Runtime.SiloControl", LogLevel.Warning);
                    _.AddConsole();
                })
                .AddMemoryGrainStorageAsDefault()
                .AddMemoryGrainStorage("PubSubStore")
                .UseDashboard()
                .Build();

            host.StartAsync().Wait();

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                host.StopAsync().Wait();
            };

            host.Stopped.Wait();
        }
    }
}
