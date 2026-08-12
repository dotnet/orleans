// <kinesis_hosting_using>
using Orleans.Hosting;

// </kinesis_hosting_using>

// <kinesis_streams_using>
using Orleans.Streams;

// </kinesis_streams_using>

using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;

namespace Documentation.Streaming;

internal static class KinesisSnippets
{
    internal static void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        // <configure_kinesis_silo>
siloBuilder
    .AddDynamoDBGrainStorage("PubSubStore", options =>
    {
        options.Service = "us-east-1";
        options.ServiceId = "orders";
        options.TableName = "OrdersPubSub";
        options.UseProvisionedThroughput = false;
    })
    .AddKinesisStreams("Orders", stream =>
    {
        stream.ConfigureKinesis(options =>
        {
            options.StreamName = "orders";
            options.Region = "us-east-1";
        });

        stream.UseDynamoDBCheckpointer(options =>
        {
            options.Service = "us-east-1";
            options.TableName = "OrdersStreamCheckpoints";
            options.PersistInterval = TimeSpan.FromSeconds(30);
        });
    });
        // </configure_kinesis_silo>
    }

    internal static void ConfigureClient(IClientBuilder clientBuilder)
    {
        // <configure_kinesis_client>
clientBuilder.AddKinesisStreams("Orders", options =>
{
    options.StreamName = "orders";
    options.Region = "us-east-1";
});
        // </configure_kinesis_client>
    }

    internal static void ConfigureGrainCheckpoints(ISiloBuilder siloBuilder)
    {
        // <configure_grain_checkpoints>
siloBuilder.AddKinesisStreams("Orders", stream =>
{
    stream.ConfigureKinesis(options =>
    {
        options.StreamName = "orders";
        options.Region = "us-east-1";
    });

    stream.UseGrainCheckpointer(options => options.Configure(options =>
    {
        options.StorageProviderName = "PubSubStore";
        options.CheckpointComparer = StreamCheckpointComparers.Numeric;
        options.PersistInterval = TimeSpan.FromSeconds(30);
    }));
});
        // </configure_grain_checkpoints>
    }
}
