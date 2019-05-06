using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Grains;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

namespace Silo
{
    public class Program
    {
        public static Task Main()
        {
            var siloPort = GetAvailablePort(11111, 11119);
            var gatewayPort = GetAvailablePort(30000, 30009);
            var healthCheckPort = GetAvailablePort(8880, 8889);

            Console.Title = $"Silo: {siloPort}, Gateway: {gatewayPort}, HealthCheck: {healthCheckPort}";

            return new HostBuilder()
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

                    services
                        .AddHostedService<HealthCheckHostedService>()
                        .Configure<HealthCheckHostedServiceOptions>(options =>
                        {
                            options.Port = healthCheckPort;
                            options.PathString = "/health";
                        });
                })
                .UseOrleans(builder =>
                {
                    builder.UseLocalhostClustering(siloPort, gatewayPort, new IPEndPoint(IPAddress.Loopback, 11111));
                    builder.AddMemoryGrainStorageAsDefault();
                    builder.ConfigureApplicationParts(manager =>
                    {
                        manager.AddApplicationPart(typeof(LocalHealthCheckGrain).Assembly).WithReferences();
                    });
                })
                .RunConsoleAsync();
        }

        public static int GetAvailablePort(int start, int end)
        {
            for (var port = start; port < end; ++port)
            {
                var listener = TcpListener.Create(port);
                listener.ExclusiveAddressUse = true;
                try
                {
                    listener.Start();
                    return port;
                }
                catch (SocketException)
                {
                    continue;
                }
                finally
                {
                    listener.Stop();
                }
            }

            throw new InvalidOperationException();
        }
    }
}
