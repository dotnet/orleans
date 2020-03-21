using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace VotingWeb
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var client = new ClientBuilder()
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "dev";
                    options.ServiceId = "votingapp";
                })
                .UseAzureStorageClustering(options => options.ConnectionString = Environment.GetEnvironmentVariable("CLUSTERING_CONNECTION_STRING"))
                .ConfigureLogging(logging => logging.AddConsole())
                .Build();

            await client.Connect(async ex =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                return true;
            });

            var webHost = WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>()
                .UseApplicationInsights()
                .ConfigureLogging(logging => logging.AddConsole())
                .ConfigureServices(services => services.AddSingleton(client))
                .Build();
            await webHost.RunAsync();
        }
    }
}
