using System.Threading.Tasks;
using Grains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

namespace Silo
{
    public class Program
    {
        public static Task Main(string[] args)
        {
            return new HostBuilder()
                .ConfigureLogging(configure =>
                {
                    configure.AddConsole();
                })
                .UseOrleans(configure =>
                {
                    configure.UseLocalhostClustering();
                    configure.ConfigureApplicationParts(manager =>
                    {
                        manager.AddApplicationPart(typeof(BasicGrain).Assembly).WithReferences();
                    });
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
