using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Presence.Grains;

namespace Presence.Silo
{
    public class Program
    {
        public static Task Main(string[] args)
        {
            Console.Title = nameof(Silo);

            return new HostBuilder()
                .UseOrleans(builder =>
                {
                    builder
                        .UseLocalhostClustering()
                        .ConfigureApplicationParts(manager =>
                         {
                             manager.AddApplicationPart(typeof(GameGrain).Assembly).WithReferences();
                         });
                })
                .ConfigureLogging(builder =>
                {
                    builder.AddConsole();
                })
                .RunConsoleAsync();
        }
    }
}
