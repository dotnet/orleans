using System;
using System.Threading.Tasks;
using Grains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

namespace Silo
{
    class Program
    {
        static Task Main(string[] args)
        {
            return new HostBuilder()
                .UseOrleans(builder =>
                {
                    builder
                        .UseLocalhostClustering()
                        .ConfigureApplicationParts(manager =>
                        {
                            manager.AddApplicationPart(typeof(ProducerGrain).Assembly).WithReferences();
                        })
                        .AddStartupTask(async (provider, token) =>
                        {
                            var factory = provider.GetService<IGrainFactory>();
                            await factory.GetGrain<IProducerGrain>("A").StartAsync(1, TimeSpan.FromSeconds(1));
                            await factory.GetGrain<IProducerGrain>("B").StartAsync(10, TimeSpan.FromSeconds(3));
                        });
                })
                .ConfigureLogging(builder =>
                {
                    builder.AddConsole();
                })
                .ConfigureServices(services =>
                {
                    services.Configure<ConsoleLifetimeOptions>(options =>
                    {
                        options.SuppressStatusMessages = true;
                    });
                })
                .RunConsoleAsync();
        }
    }
}
