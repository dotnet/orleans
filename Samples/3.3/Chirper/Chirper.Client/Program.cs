using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

namespace Chirper.Client
{
    class Program
    {
        static Task Main(string[] args)
        {
            Console.Title = nameof(Client);

            return new HostBuilder()

                .ConfigureServices(services => services

                    .AddSingleton<ClusterClientHostedService>()
                    .AddSingleton<IHostedService>(_ => _.GetService<ClusterClientHostedService>())
                    .AddSingleton(_ => _.GetService<ClusterClientHostedService>().Client)
                    .AddSingleton<IHostedService, ShellHostedService>()
                    .Configure<ConsoleLifetimeOptions>(_ =>
                    {
                        _.SuppressStatusMessages = true;
                    }))

                .ConfigureLogging(builder => builder

                    .AddDebug())

                .RunConsoleAsync();
        }
    }
}
