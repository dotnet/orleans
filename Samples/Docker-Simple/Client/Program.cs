using GrainInterfaces;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Client
{
    class Program
    {
        static void Main(string[] args)
        {
            MainAsync().GetAwaiter().GetResult();
        }

        static async Task MainAsync()
        {
            var connectionString = File.ReadAllText("connection-string.txt");

            var config = new ClientConfiguration
            {
                GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable,
                DataConnectionString = connectionString
            };

            var client = new ClientBuilder()
                .AddApplicationPartsFromBasePath()
                .UseConfiguration(config)
                .Build();

            await client.Connect();

            var grain = client.GetGrain<IPingGrain>(Guid.NewGuid());

            for (int i = 0; i < 10; i++)
            {
                var value = await grain.Ping();
                Thread.Sleep(500);
                Console.WriteLine($"Ping: {value}");
            }
        }
    }
}
