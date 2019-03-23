using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

namespace Chirper.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = nameof(Client);

            var client = new ClientBuilder()
                .UseLocalhostClustering()
                .ConfigureLogging(_ =>
                {
                    _.AddDebug();
                })
                .Build();

            var logger = client.ServiceProvider.GetService<ILoggerProvider>().CreateLogger(typeof(Program).FullName);

            Console.WriteLine("Connecting...");

            var retries = 100;
            client.Connect(async error =>
            {
                if (--retries < 0)
                {
                    logger.LogError("Could not connect to the cluster: {@Message}", error.Message);
                    return false;
                }
                else
                {
                    logger.LogWarning(error, "Error Connecting: {@Message}", error.Message);
                }
                await Task.Delay(1000);
                return true;
            }).Wait();

            Console.WriteLine("Connected.");

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                client.Close().Wait();
                Environment.Exit(0);
            };

            new Shell(client)
                .RunAsync(client)
                .Wait();
        }
    }
}
