using System;
using System.Threading.Tasks;
using Common;
using GrainInterfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Serialization;

namespace SiloHost
{
    class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                var host = new HostBuilder()
                    .UseOrleans(ConfigureSilo)
                    .ConfigureLogging(logging => logging.AddConsole())
                    .Build();

                await host.RunAsync();

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void ConfigureSilo(ISiloBuilder siloBuilder)
        {
            var secrets = Secrets.LoadFromFile();
            siloBuilder
                .UseLocalhostClustering(serviceId: Constants.ServiceId, clusterId: Constants.ServiceId)
                .AddAzureTableGrainStorage("PubSubStore", options => options.ConnectionString = secrets.DataConnectionString)
                .AddEventHubStreams(Constants.StreamProvider, b =>
                {
                    b.ConfigureEventHub(ob => ob.Configure(options =>
                    {
                        options.ConnectionString = secrets.EventHubConnectionString;
                        options.ConsumerGroup = Constants.EHConsumerGroup;
                        options.Path = Constants.EHPath;

                    }));
                    b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                    {
                        options.ConnectionString = secrets.DataConnectionString;
                        options.PersistInterval = TimeSpan.FromSeconds(10);
                    }));
                });
        }
    }
}
