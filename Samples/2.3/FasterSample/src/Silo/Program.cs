using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Silo
{
    public class Program
    {
        public static Task Main()
        {
            return new HostBuilder()
                .UseOrleans(_ =>
                {
                    _.UseLocalhostClustering();
                })
                .ConfigureLogging(_ =>
                {
                    _.AddConsole();
                })
                .ConfigureServices(sc =>
                {
                    sc.Configure<ConsoleLifetimeOptions>(_ =>
                    {
                        _.SuppressStatusMessages = true;
                    });
                })
                .RunConsoleAsync();
        }
    }
}