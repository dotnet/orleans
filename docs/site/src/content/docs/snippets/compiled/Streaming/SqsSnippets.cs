using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Streaming.SQS.Streams;

namespace Documentation.Streaming;

internal static class SqsSnippets
{
    internal static void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        // <configure_sqs_silo>
        siloBuilder
            .AddDynamoDBGrainStorage("PubSubStore", options =>
            {
                options.Service = "us-east-1";
                options.TableName = "OrdersPubSub";
            })
            .AddSqsStreams("Orders", options =>
            {
                options.ConnectionString = "Service=us-east-1";
                options.ReceiveWaitTimeSeconds = 20;
                options.VisibilityTimeoutSeconds = 60;
            });
        // </configure_sqs_silo>
    }

    internal static void ConfigureClient(IClientBuilder clientBuilder)
    {
        // <configure_sqs_client>
        clientBuilder.AddSqsStreams("Orders", options =>
        {
            options.ConnectionString = "Service=us-east-1";
        });
        // </configure_sqs_client>
    }

    internal static void ConfigureFifoSilo(ISiloBuilder siloBuilder)
    {
        // <configure_sqs_fifo>
        siloBuilder.AddSqsStreams("Orders", streams =>
        {
            streams.ConfigurePartitioning(16);
            streams.ConfigureSqs(options => options.Configure(sqs =>
            {
                sqs.ConnectionString = "Service=us-east-1";
                sqs.FifoQueue = true;
                sqs.ReceiveWaitTimeSeconds = 20;
                sqs.VisibilityTimeoutSeconds = 60;
            }));
        });
        // </configure_sqs_fifo>
    }

    internal static void ConfigureCustomAdapter(
        ISiloBuilder siloBuilder,
        IClientBuilder clientBuilder)
    {
        // <configure_sqs_data_adapter>
        siloBuilder.AddSqsStreams("Orders", streams =>
        {
            streams.ConfigureSqs(options => options.Configure(sqs =>
            {
                sqs.ConnectionString = "Service=us-east-1";
                sqs.ReceiveMessageAttributes = ["SchemaVersion", "ContentType"];
            }));
            streams.UseDataAdapter((services, _) =>
                ActivatorUtilities.CreateInstance<ApplicationSqsDataAdapter>(services));
        });

        clientBuilder.AddSqsStreams("Orders", streams =>
        {
            streams.ConfigureSqs(options => options.Configure(sqs =>
            {
                sqs.ConnectionString = "Service=us-east-1";
            }));
            streams.UseDataAdapter((services, _) =>
                ActivatorUtilities.CreateInstance<ApplicationSqsDataAdapter>(services));
        });
        // </configure_sqs_data_adapter>
    }

    private sealed class ApplicationSqsDataAdapter(Serializer serializer)
        : SQSDataAdapter(serializer);
}
