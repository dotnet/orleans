using GrainInterfaces;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using System;
using System.IO;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace Client
{
    class Program
    {
        private const int NumberOfPing = 5;
        private static bool isStopping = false;
        private static readonly ManualResetEvent clientStopped = new ManualResetEvent(false);

        static void Main(string[] args)
        {
            MainAsync().GetAwaiter().GetResult();
        }

        static async Task MainAsync()
        {
            AssemblyLoadContext.Default.Unloading += context =>
            {
                isStopping = true;
                clientStopped.WaitOne();
            };

            var connectionString = File.ReadAllText("connection-string.txt");

            var config = new ClientConfiguration
            {
                ClusterId = "orleans-docker",
                GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable,
                DataConnectionString = connectionString
            };

            var client = new ClientBuilder()
                .ConfigureApplicationParts(parts =>
                        parts.AddApplicationPart(typeof(IPingGrain).Assembly).WithReferences())
                .UseConfiguration(config)
                .Build();

            await client.Connect();
            Console.WriteLine("Client is connected");

            while (!isStopping)
            {
                var grainId = Guid.NewGuid();
                var grain = client.GetGrain<IPingGrain>(grainId);
                Console.WriteLine($"Pinging grain {grainId} {NumberOfPing} times");
                Console.WriteLine($"  This grain is activated on {await grain.GetRuntimeIdentity()}");

                for (var i = 0; i < NumberOfPing; i++)
                {
                    if (isStopping) break;

                    var value = await grain.Ping();
                    Console.WriteLine($"  Ping  #{value}");
                    Thread.Sleep(500);
                }
            }

            Console.WriteLine("Client is stopping");

            clientStopped.Set();
        }
    }
}
