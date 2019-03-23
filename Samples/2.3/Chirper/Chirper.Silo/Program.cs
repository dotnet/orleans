using System;
using System.Threading.Tasks;
using Chirper.Grains;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

namespace Chirper.Silo
{
    class Program
    {
        static Task Main(string[] args)
        {
            Console.Title = nameof(Silo);

            return new HostBuilder()

                .UseOrleans(builder => builder

                    .UseLocalhostClustering()
                    .ConfigureApplicationParts(_ => _.AddApplicationPart(typeof(ChirperAccount).Assembly).WithReferences())
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("PubSubStore")
                    .UseDashboard())

                .ConfigureLogging(builder => builder

                    .AddFilter("Orleans.Runtime.Management.ManagementGrain", LogLevel.Warning)
                    .AddFilter("Orleans.Runtime.SiloControl", LogLevel.Warning)
                    .AddConsole())              

                .RunConsoleAsync();
        }
    }
}
