using GrainInterfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Orleans.Streams;
using Common;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        const int maxAttempts = 5;
        var attempt = 0;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var secrets = Secrets.TryLoadFromFile();
            var useEventHub = secrets is not null;

            var host = new HostBuilder()
                .ConfigureLogging(logging => logging.AddConsole())
                .UseOrleansClient((context, client) =>
                {
                    client
                        .UseLocalhostClustering(serviceId: Constants.ServiceId, clusterId: Constants.ClusterId)
                        .UseConnectionRetryFilter(RetryFilter);

                    if (useEventHub)
                    {
                        // Use Azure Event Hub streaming
                        client.AddEventHubStreams(
                            Constants.StreamProvider,
                            configurator => configurator.ConfigureEventHub(
                                builder => builder.Configure(options =>
                                {
                                    options.ConfigureEventHubConnection(
                                        secrets!.EventHubConnectionString,
                                        Constants.EHPath,
                                        Constants.EHConsumerGroup);
                                })));
                        Console.WriteLine("Using Azure Event Hub streaming");
                    }
                    else
                    {
                        // Use in-memory streaming for local development
                        // Memory streams are handled by the silo, client just needs the provider registered
                        client.AddMemoryStreams(Constants.StreamProvider);
                        Console.WriteLine("Using in-memory streaming (no Secrets.json found)");
                    }
                })
                .Build();

            await host.StartAsync(cts.Token);
            Console.WriteLine("Client successfully connected to silo host");
            Console.WriteLine("Press Ctrl+C to stop");

            var clusterClient = host.Services.GetRequiredService<IClusterClient>();

            // Use the connected client to ask a grain to start producing events
            var key = Guid.NewGuid();
            var producer = clusterClient.GetGrain<IProducerGrain>("my-producer");
            await producer.StartProducing(Constants.StreamNamespace, key);

            // Now you should see that a consumer grain was activated on the silo, and is logging when it is receiving event

            // Client can also subscribe to streams
            var streamId = StreamId.Create(Constants.StreamNamespace, key);
            var stream = clusterClient
                .GetStreamProvider(Constants.StreamProvider)
                .GetStream<int>(streamId);
            await stream.SubscribeAsync(OnNextAsync);

            // Now the client will also log received events

            if (useEventHub && args.Contains("--pause-after-checkpoint", StringComparer.OrdinalIgnoreCase))
            {
                var checkpointWindow = TimeSpan.FromSeconds(20);
                Console.WriteLine(
                    $"Allowing {checkpointWindow} for the 10-second checkpoint interval before pausing stream delivery.");
                await Task.Delay(checkpointWindow, cts.Token);

                var management = clusterClient.GetGrain<IManagementGrain>(0);
                await management.SendControlCommandToProvider<PersistentStreamProvider>(
                    Constants.StreamProvider,
                    (int)PersistentStreamProviderCommand.StopAgents);

                Console.WriteLine(
                    "Stream pulling agents are paused. The producer remains active; record the next ten event numbers from the silo log, then terminate the silo.");
            }

            // Wait until cancelled
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when Ctrl+C is pressed
            }

            // Stop producing
            Console.WriteLine("Stopping producer...");
            await producer.StopProducing();

            await host.StopAsync();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
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
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            return true;
        }
    }
}
