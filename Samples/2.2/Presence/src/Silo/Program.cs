using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Presence.Grains;

namespace Presence.Silo
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            Console.Title = nameof(Silo);

            var host = new SiloHostBuilder()
                .UseLocalhostClustering()
                .ConfigureApplicationParts(_ =>
                {
                    _.AddApplicationPart(typeof(GameGrain).Assembly).WithReferences();
                })
                .ConfigureLogging(_ =>
                {
                    _.AddConsole();
                })
                .Build();

            await host.StartAsync();

            Console.CancelKeyPress += async (sender, eargs) =>
            {
                eargs.Cancel = true;
                await host.StopAsync();
            };

            await host.Stopped;
        }
    }
}
