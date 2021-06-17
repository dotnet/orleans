using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Common;

namespace NonOrleansClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var secrets = Secrets.LoadFromFile();

            // Sending event to a stream
            // Here the StreamGuid will be encoded as the PartitionKey, and the namespace as a property of the event

            await using (var client = new EventHubProducerClient(secrets.EventHubConnectionString, Constants.EHPath))
            {
                var key = Guid.NewGuid().ToString();
                var options = new SendEventOptions { PartitionKey = Guid.NewGuid().ToString() };

                Console.WriteLine($"Sending event to StreamId: [{key}, {Constants.StreamNamespace}]");

                for (int i = 0; i < 30; i++)
                {
                    Console.WriteLine($"Sending '{i}'");

                    var evt = new EventData(JsonSerializer.SerializeToUtf8Bytes(i));
                    evt.Properties["StreamNamespace"] = Constants.StreamNamespace;

                    await client.SendAsync(new[] { evt }, options);

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
            Console.WriteLine("Done!");
        }
    }
}
