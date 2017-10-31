using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime.Configuration;

namespace OrleansClient
{
    class Program
    {
        private static IClusterClient client;
        private static bool running;

        static void Main(string[] args)
        {
            Task.Run(() => InitializeOrleans());

            Console.ReadLine();

            running = false;
        }

        static async Task InitializeOrleans()
        {
            var config = new ClientConfiguration();
            config.PropagateActivityId = true;
            config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, 10400));

            Console.WriteLine("Initializing...");

            client = new ClientBuilder().UseConfiguration(config).Build();
            await client.Connect();
            running = true;
            Console.WriteLine("Initialized!");

            var grain = client.GetGrain<IHellogGrain>(Guid.NewGuid());

            while(running)
            {
                var response = await grain.SayHello("Hello Gutemberg!");
                Console.WriteLine($"[{DateTime.UtcNow}] - {response}");
                await Task.Delay(1000);
            }
            client.Dispose();
        }
    }
}
