# RabbitMQ Streams

This sample runs an Orleans silo with persistent streams backed by RabbitMQ Streams. A producer grain publishes an event every two seconds, and an implicitly subscribed consumer grain logs each event.

## Prerequisites

- The .NET SDK selected by the repository's `global.json`.
- A container runtime supported by [Aspire](https://aspire.dev).

## Run the sample

From the repository root:

```shell
dotnet run --project samples/Streaming/RabbitMQ/RabbitMQ.AppHost
```

The Aspire dashboard shows the RabbitMQ and Orleans resources, their logs, and a link to the RabbitMQ management UI. Aspire generates the RabbitMQ password and injects the credentials and stream endpoint into the silo.

Press <kbd>Ctrl</kbd>+<kbd>C</kbd> to stop the sample and its resources.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `RABBITMQ_STREAM_ADDRESS` | `127.0.0.1` | RabbitMQ Streams host name or IP address |
| `RABBITMQ_STREAM_PORT` | `5552` | RabbitMQ Streams port |
| `RABBITMQ_STREAM_USER` | `guest` | RabbitMQ username |
| `RABBITMQ_STREAM_PASSWORD` | `guest` | RabbitMQ password |
| `ORLEANS_RABBITMQ_PARTITIONS` | `4` | Orleans stream queue count |

The AppHost supplies the RabbitMQ endpoint and credential variables. Set them directly only when running `RabbitMQ.Silo.csproj` without the AppHost.

The provider creates each RabbitMQ stream with a 200 MiB maximum length by default. Set `RabbitMQClientOptions.StreamOptions.MaxLengthBytes` in the silo and any standalone client configuration to choose a different retention capacity.

See [Stream with RabbitMQ](../../../docs/site/src/content/docs/streaming/rabbitmq-streaming.md) for production configuration and delivery guidance.
