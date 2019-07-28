using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Sample.Grains;
using Sample.Silo.Api;
using Serilog;
using Serilog.Events;

namespace Sample.Silo
{
    public class Program
    {
        public static Task Main(string[] args)
        {
            return new HostBuilder()
                .ConfigureAppConfiguration(builder =>
                {
                    builder.AddCommandLine(args);
                })
                .ConfigureLogging(builder =>
                {
                    builder.AddFilter("Orleans.Runtime.Management.ManagementGrain", LogLevel.Warning);
                    builder.AddFilter("Orleans.Runtime.SiloControl", LogLevel.Warning);
                    builder.AddSerilog(new LoggerConfiguration()
                        .WriteTo.Console()
                        .Filter.ByExcluding(e => e.Properties["SourceContext"].ToString() == @"""Orleans.Runtime.Management.ManagementGrain""" && e.Level < LogEventLevel.Warning)
                        .Filter.ByExcluding(e => e.Properties["SourceContext"].ToString() == @"""Orleans.Runtime.SiloControl""" && e.Level < LogEventLevel.Warning)
                        .CreateLogger());
                })
                .ConfigureServices(services =>
                {
                    services.Configure<ConsoleLifetimeOptions>(options =>
                    {
                        options.SuppressStatusMessages = true;
                    });

                    services.AddHostedService<ApiService>();
                })
                .UseOrleans(builder =>
                {
                    builder.ConfigureApplicationParts(manager =>
                    {
                        manager.AddApplicationPart(typeof(WeatherGrain).Assembly).WithReferences();
                    });
                    builder.UseLocalhostClustering();
                    builder.AddMemoryGrainStorageAsDefault();
                    builder.AddSimpleMessageStreamProvider("SMS");
                    builder.AddMemoryGrainStorage("PubSubStore");
                    builder.UseDashboard(options =>
                    {
                        options.HideTrace = true;
                    });
                })
                .RunConsoleAsync();
        }
    }
}