using GrainInterfaces;
using Orleans;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Orleans.Streams;
using Orleans.Hosting;
using Common;
using Microsoft.Extensions.DependencyInjection;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        const int maxAttempts = 5;
        var attempt = 0;

        try
        {
            var secrets = Secrets.LoadFromFile()!;
            var host = new HostBuilder()
                .ConfigureLogging(logging => logging.AddConsole())
                .UseOrleansClient((context, client) =>
                {
                    client
                        .UseLocalhostClustering(serviceId: Constants.ServiceId, clusterId: Constants.ClusterId)
                        .UseConnectionRetryFilter(RetryFilter)
                        .AddEventHubStreams(
                            Constants.StreamProvider,
                            (configurator) => configurator.ConfigureEventHub(
                                builder => builder.Configure(options =>
                                {
                                    options.ConfigureEventHubConnection(
                                        secrets.EventHubConnectionString,
                                        Constants.EHPath,
                                        Constants.EHConsumerGroup);
                                })));
                })
                .Build();

            await host.StartAsync();
            Console.WriteLine("Client successfully connect to silo host");

            var client = host.Services.GetRequiredService<IClusterClient>();

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

        static Task OnNextAsync(int item, StreamSequenceToken? token = null)
        {
            Console.WriteLine("OnNextAsync: item: {0}, token = {1}", item, token);
            return Task.CompletedTask;
        }

        async Task<bool> RetryFilter(Exception exception, CancellationToken cancellationToken)
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