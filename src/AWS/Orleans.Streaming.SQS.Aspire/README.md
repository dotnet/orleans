# Microsoft Orleans Aspire integration for Amazon SQS

The `Microsoft.Orleans.Streaming.SQS.Aspire` package configures an Orleans SQS stream provider and provisions its complete partition queue topology through the AWS CDK integration for .NET Aspire.

## Install

```shell
dotnet add package Microsoft.Orleans.Streaming.SQS.Aspire
```

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

## Documentation

See [Stream with Amazon SQS](https://dotnet.github.io/orleans/docs/streaming/sqs-streaming/) for runtime behavior, permissions, delivery semantics, and operations.
