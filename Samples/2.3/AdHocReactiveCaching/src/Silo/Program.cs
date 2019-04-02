using System;
using System.Threading.Tasks;
using Grains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Silo
{
    class Program
    {
        static Task Main(string[] args)
        {
            Console.Title = nameof(Silo);

            return new HostBuilder()
                .UseOrleans(builder =>
                {
                    builder
                        .UseLocalhostClustering()
                        .ConfigureApplicationParts(manager =>
                        {
                            manager.AddApplicationPart(typeof(ProducerGrain).Assembly).WithReferences();
                        })
                        .Configure<SiloMessagingOptions>(options =>
                        {
                            // reduced message timeout to ease promise break testing
                            options.ResponseTimeout = TimeSpan.FromSeconds(10);
                            options.ResponseTimeoutWithDebugger = TimeSpan.FromSeconds(10);
                        })
                        .Configure<ClientMessagingOptions>(options =>
                        {
                            // reduced message timeout to ease promise break testing
                            options.ResponseTimeout = TimeSpan.FromSeconds(10);
                            options.ResponseTimeoutWithDebugger = TimeSpan.FromSeconds(10);
                        })
                        .AddStartupTask(async (provider, token) =>
                        {
                            var factory = provider.GetService<IGrainFactory>();
                            var client = provider.GetService<IClusterClient>();

                            // make the first producer grain change every five seconds
                            await factory.GetGrain<IProducerGrain>("A").StartAsync(1, TimeSpan.FromSeconds(5));

                            // make the second producer grain change every fifteen seconds
                            await factory.GetGrain<IProducerGrain>("B").StartAsync(10, TimeSpan.FromSeconds(15));
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
