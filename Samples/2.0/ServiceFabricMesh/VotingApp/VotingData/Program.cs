using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using VotingContract;

namespace VotingData
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var silo = new SiloHostBuilder()
                .ConfigureEndpoints(siloPort: 30001, gatewayPort: 30002, listenOnAnyHostAddress: true)
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "dev";
                    options.ServiceId = "votingapp";
                })
                .UseAzureStorageClustering(options => options.ConnectionString = Environment.GetEnvironmentVariable("CLUSTERING_CONNECTION_STRING"))
                .ConfigureLogging(logging => logging.AddConsole())
                .AddAzureBlobGrainStorage("votes",
                    options =>
                    {
                        options.ContainerName = "votes";
                        options.UseJson = true;
                        options.ConnectionString = Environment.GetEnvironmentVariable("PERSISTENCE_CONNECTION_STRING");
                    })
                .Build();

            var stopEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = false;
                stopEvent.Set();
            };

            Console.WriteLine("Starting");
            await silo.StartAsync();
            Console.WriteLine("Started");

            stopEvent.WaitOne();
            Console.WriteLine("Shutting down");
            await silo.StopAsync();
        }
    }
}
