using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Stocks.Grains;

namespace SiloHost
{
    public class Program
    {
        public static Task Main(string[] args)
        {
            return new HostBuilder()
                .UseOrleans(builder =>
                {
                    builder
                        .UseLocalhostClustering()
                        .Configure<ClusterOptions>(options =>
                        {
                            options.ClusterId = "dev";
                            options.ServiceId = "StocksSampleApp";
                        })
                        .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(StockGrain).Assembly).WithReferences())
                        .ConfigureLogging(logging => logging.AddConsole());
                })
                .ConfigureServices(services =>
                {
                    services.AddHostedService<StocksHostedService>();
                    services.Configure<ConsoleLifetimeOptions>(options =>
                    {
                        options.SuppressStatusMessages = true;
                    });
                })
                .RunConsoleAsync();
        }
    }
}
