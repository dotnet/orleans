# Microsoft Orleans Streaming for Amazon SQS

The `Microsoft.Orleans.Streaming.SQS` package connects Orleans persistent streams to Amazon Simple Queue Service (SQS). It supports standard and FIFO queues, configurable queue partitioning and receive behavior, and custom data adapters for application-defined wire formats.

## Install

```shell
dotnet add package Microsoft.Orleans.Streaming.SQS
```

## Configure

Register the same named provider on the silo and every Orleans client which publishes through it:

```csharp
siloBuilder.AddSqsStreams("Orders", options =>
{
    options.ConnectionString = "Service=us-east-1";
    options.ReceiveWaitTimeSeconds = 20;
    options.VisibilityTimeoutSeconds = 60;
});

clientBuilder.AddSqsStreams("Orders", options =>
{
    options.ConnectionString = "Service=us-east-1";
});
```

When explicit credentials aren't present in the connection string, the provider uses the AWS SDK credential resolution chain. Prefer workload credentials such as an IAM role.

## Documentation

See [Stream with Amazon SQS](https://dotnet.github.io/orleans/docs/streaming/sqs-streaming/) for FIFO queues, custom data adapters, delivery semantics, permissions, tuning, and operational guidance.

## Feedback and contributing

- [Open an Orleans issue](https://github.com/dotnet/orleans/issues)
- [Join the Orleans community on Discord](https://aka.ms/orleans-discord)
- Review the [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
