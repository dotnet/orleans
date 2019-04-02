using System;
using System.Threading.Tasks;
using Client.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Client.B
{
    class Program
    {
        public static Task Main()
        {
            Console.Title = "Client.B";

            return new HostBuilder()
                .ConfigureServices(services =>
                {
                    // this hosted service connects and disconnects from the cluster along with the host
                    // it also exposes the cluster client to other services that request it
                    services.AddSingleton<ClusterClientHostedService>();
                    services.AddSingleton<IHostedService>(_ => _.GetService<ClusterClientHostedService>());
                    services.AddSingleton(_ => _.GetService<ClusterClientHostedService>().Client);

                    // this hosted service runs the sample logic
                    services.AddSingleton<IHostedService, CacheTesterHostedService>();

                    // this configures the test running on this particular client
                    services.Configure<CacheTesterOptions>(options =>
                    {
                        options.PublisherCacheGrainKey = "B";
                    });

                    // this tells the generic host to stay quiet
                    services.Configure<ConsoleLifetimeOptions>(options =>
                    {
                        options.SuppressStatusMessages = true;
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
