using System;
using System.Threading.Tasks;
using GrainInterfaces;
using Orleans;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Hosting;
using Orleans.Providers;
using Common;

namespace Client
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            try
            {
                var secrets = Secrets.LoadFromFile();
                // Configure a client and connect to the service.
                var client = new ClientBuilder()
                    .UseLocalhostClustering(serviceId: Constants.ServiceId, clusterId: Constants.ClusterId)
                    .AddEventHubStreams(Constants.StreamProvider, b =>
                    {
                        b.ConfigureEventHub(ob => ob.Configure(options =>
                        {
                            options.ConnectionString = secrets.EventHubConnectionString;
                            options.ConsumerGroup = Constants.EHConsumerGroup;
                            options.Path = Constants.EHPath;

                        }));
                    })
                    .ConfigureLogging(logging => logging.AddConsole())
                    .Build();

                await client.Connect(CreateRetryFilter());
                Console.WriteLine("Client successfully connect to silo host");

                // Use the connected client to ask a grain to start producing events
                var key = Guid.NewGuid();
                var producer = client.GetGrain<IProducerGrain>("my-producer");
                await producer.StartProducing(Constants.StreamNamespace, key);

                // Now you should see that a consumer grain was activated on the silo, and is logging when it is receiving event

                // Client can also subscribe to streams
                var stream = client
                    .GetStreamProvider(Constants.StreamProvider)
                    .GetStream<int>(key, Constants.StreamNamespace);
                await stream.SubscribeAsync(OnNextAsync);

                // Now the client will also log received events

                await Task.Delay(TimeSpan.FromSeconds(15));

                // Stop producing
                await producer.StopProducing();

                Console.ReadKey();
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Console.ReadKey();
                return 1;
            }
        }

        private static Task OnNextAsync(int item, StreamSequenceToken token = null)
        {
            Console.WriteLine("OnNextAsync: item: {0}, token = {1}", item, token);
            return Task.CompletedTask;
        }

        private static Func<Exception, Task<bool>> CreateRetryFilter(int maxAttempts = 5)
        {
            var attempt = 0;
            return RetryFilter;

            async Task<bool> RetryFilter(Exception exception)
            {
                attempt++;
                Console.WriteLine($"Cluster client attempt {attempt} of {maxAttempts} failed to connect to cluster.  Exception: {exception}");
                if (attempt > maxAttempts)
                {
                    return false;
                }

                await Task.Delay(TimeSpan.FromSeconds(4));
                return true;
            }
        }
    }
}
