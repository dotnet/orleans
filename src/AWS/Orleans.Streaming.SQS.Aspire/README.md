# Microsoft Orleans Aspire integration for Amazon SQS

The `Microsoft.Orleans.Streaming.SQS.Aspire` package configures an Orleans SQS stream provider and provisions its complete partition queue topology through the AWS CDK integration for .NET Aspire.

## Install

```shell
dotnet add package Microsoft.Orleans.Streaming.SQS.Aspire
```

Install `Microsoft.Orleans.Streaming.SQS` in every silo and Orleans client project that uses the provider.

## Configure

```csharp
using Amazon;
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var aws = builder.AddAWSSDKConfig()
    .WithRegion(RegionEndpoint.USEast1);

var orleans = builder.AddOrleans("cluster")
    .WithDevelopmentClustering()
    .WithMemoryGrainStorage("PubSubStore")
    .WithSqsStreaming(
        "Orders",
        aws,
        new SqsStreamingOptions
        {
            ServiceId = "orders-service",
            PartitionCount = 16,
            FifoQueue = true,
            ReceiveWaitTimeSeconds = 20,
            VisibilityTimeoutSeconds = 60,
        });

builder.AddProject<Projects.Silo>("silo")
    .WithReference(orleans);

builder.AddProject<Projects.Client>("client")
    .WithReference(orleans.AsClient());
```

The options define both the AWS CDK queue resources and the Orleans configuration emitted to every referenced silo and client. The integration applies the stable service ID, attaches the AWS SDK profile and region, and makes each referenced resource wait for the CDK stack automatically.

Provider names are unique ignoring case within an Orleans service, including providers registered through other streaming extensions. Each physical queue has one owning SQS stack per AppHost and region. Registration validates both identities before adding resources or changing Orleans configuration, and reports the conflicting provider or queue. Use distinct service IDs or provider names for separate topologies in the same region, including when AWS profiles select different accounts.

Physical queue names preserve the service ID and lowercase the provider name: `orders-service-orders_primary-0`. Hyphens and underscores remain distinct in physical names; the CDK resource identifiers distinguish names such as `orders-primary` and `orders_primary`.

## Documentation

See [Stream with Amazon SQS](https://dotnet.github.io/orleans/docs/streaming/sqs-streaming/) for runtime behavior, permissions, delivery semantics, and operations.
