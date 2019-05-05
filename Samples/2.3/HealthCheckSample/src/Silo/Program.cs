using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace Silo
{
    public class Program
    {
        public static Task Main()
        {
            return new HostBuilder()
                .UseOrleans(builder =>
                {
                    builder.UseLocalhostClustering();
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
                    services.AddHostedService<HealthCheckHostedService>();
                })
                .RunConsoleAsync();
        }
    }
}
