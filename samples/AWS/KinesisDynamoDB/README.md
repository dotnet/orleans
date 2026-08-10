# AWS Kinesis and DynamoDB

This sample runs an Orleans silo which uses AWS services for every durable subsystem:

- DynamoDB cluster membership
- DynamoDB grain state and `PubSubStore`
- DynamoDB reminders
- Amazon Kinesis Data Streams
- DynamoDB Kinesis stream checkpoints

The sample publishes an event every five seconds. An implicitly subscribed grain consumes each event, persists its state, and registers a durable reminder.

## Prerequisites

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) selected by the repository's `global.json`.
1. Configure AWS credentials using the standard [AWS SDK credential chain](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html).
1. Create a Kinesis data stream:

   ```shell
   aws kinesis create-stream --stream-name orleans-sample --shard-count 1 --region us-east-1
   aws kinesis wait stream-exists --stream-name orleans-sample --region us-east-1
   ```

The sample creates its DynamoDB tables automatically with on-demand billing. For production deployments, provision tables through infrastructure as code and disable automatic creation.

## Run the sample

From the repository root:

```shell
dotnet run --project samples/AWS/KinesisDynamoDB/KinesisDynamoDB.csproj
```

Press <kbd>Ctrl</kbd>+<kbd>C</kbd> to stop.

The following environment variables customize the resources:

| Variable | Default | Purpose |
|---|---|---|
| `AWS_REGION` or `AWS_DEFAULT_REGION` | `us-east-1` | Region for Kinesis and DynamoDB |
| `ORLEANS_KINESIS_STREAM` | `orleans-sample` | Existing Kinesis data stream |
| `ORLEANS_DYNAMODB_PREFIX` | `OrleansSample` | Prefix for all five DynamoDB tables |
| `ORLEANS_CLUSTER_ID` | `aws-kinesis-sample` | Orleans deployment identifier |
| `ORLEANS_SERVICE_ID` | `aws-kinesis-sample` | Stable Orleans application identifier |

Use a distinct cluster ID for each concurrently running deployment. Keep the service ID stable when a replacement deployment must retain grain state, reminders, subscriptions, and checkpoints.

See [Stream with Amazon Kinesis](../../../docs/site/src/content/docs/streaming/kinesis-streaming.md) for checkpoint behavior and operational guidance.
