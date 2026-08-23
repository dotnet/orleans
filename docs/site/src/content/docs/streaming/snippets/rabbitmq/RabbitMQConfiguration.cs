using System.Net;
using Orleans.Hosting;
using Orleans.Streaming.RabbitMQ.Configurators;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using RabbitMQ.Stream.Client;

namespace Orleans.Docs.Snippets.Streaming.RabbitMQ;

public static class RabbitMQConfiguration
{
    public static void Configure(ISiloBuilder siloBuilder)
    {
        // <rabbitmq_silo>
        var endpoint = new IPEndPoint(IPAddress.Loopback, 5552);

        siloBuilder
            .AddMemoryGrainStorage("PubSubStore")
            .AddRabbitMQStreams("RabbitMQ", stream =>
            {
                stream.ConfigureRabbitMQ(optionsBuilder => optionsBuilder.Configure(options =>
                {
                    options.StreamSystemConfig = new StreamSystemConfig
                    {
                        UserName = "guest",
                        Password = "guest",
                        Endpoints = [endpoint],
                        AddressResolver = new AddressResolver(endpoint),
                    };
                    options.IntervalToUpdateOffset = TimeSpan.FromSeconds(10);
                    options.ConnectionRetry = new RabbitMQConnectionRetryOptions
                    {
                        MaxAttempts = 10,
                        Delay = TimeSpan.FromSeconds(2),
                    };
                }));

                stream.ConfigurePartitioning(8);
                stream.ConfigureCache(4_096);
            });
        // </rabbitmq_silo>
    }

    public static void ConfigureClient(IClientBuilder clientBuilder)
    {
        // <rabbitmq_client>
        var endpoint = new IPEndPoint(IPAddress.Loopback, 5552);

        clientBuilder.AddRabbitMQStreams("RabbitMQ", stream =>
        {
            stream.ConfigureRabbitMQ(optionsBuilder => optionsBuilder.Configure(options =>
            {
                options.StreamSystemConfig = new StreamSystemConfig
                {
                    UserName = "guest",
                    Password = "guest",
                    Endpoints = [endpoint],
                    AddressResolver = new AddressResolver(endpoint),
                };
            }));

            stream.ConfigurePartitioning(8);
        });
        // </rabbitmq_client>
    }
}
