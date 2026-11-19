# Microsoft Orleans Durable

## Introduction
Microsoft Orleans Durable runs recoverable business operations on Orleans grains on .NET 11 (`net11.0`). Submit an order under an application-owned identity, reserve inventory, charge payment, and ship; reconnect to the same operation when the response is lost, and continue unfinished work after an activation or silo is lost.

Start with [Run a durable order workflow](https://github.com/dotnet/orleans/blob/main/docs/site/src/content/docs/grains/durable-tasks-onboarding.md) and the runnable [DurableWorkflows sample](https://github.com/dotnet/orleans/tree/main/samples/DurableWorkflows). The guide connects packages and host registration to durable acceptance, retained results, compensation, cancellation, and recovery. The sample's Aspire AppHost supplies two service replicas, Redis membership, and Azurite Blob journals and jobs.

The operation builds on four cooperating layers:

- **Journaling** commits each grain's state together with its task, inbox, and outbox changes.
- **Durable Jobs** provides persistent wake-ups for pending executions and due delays.
- **Messaging** transfers durable work and results between grains.
- **Durable Tasks** records recoverable workflow progress and business outcomes.

### Feature reference

Each feature is registered independently on the silo builder; `AddDurableTasks()` includes Messaging:

| Feature | Registration | Namespace | Guide |
|---|---|---|---|
| Journaling | `AddJournaling()` | `Orleans.Durable.Journaling` | [Journaling](https://github.com/dotnet/orleans/blob/main/src/Orleans.Durable/Journaling/README.md) |
| Durable Jobs | `AddDurableJobs()` | `Orleans.Durable.Jobs` | [Durable Jobs](https://github.com/dotnet/orleans/blob/main/src/Orleans.Durable/DurableJobs/README.md) |
| Messaging | `AddMessaging()` | `Orleans.Durable.Messaging` | [Messaging](https://github.com/dotnet/orleans/blob/main/src/Orleans.Durable/Messaging/README.md) |
| Durable Tasks | `AddDurableTasks()` | `Orleans.Durable.Tasks` | [Durable Tasks](https://github.com/dotnet/orleans/blob/main/src/Orleans.Durable/DurableTasks/README.md) |

Storage providers ship separately in `Microsoft.Orleans.Durable.AzureStorage`, `Microsoft.Orleans.Durable.S3`, and `Microsoft.Orleans.Durable.Redis`. `Microsoft.Orleans.Durable.Abstractions` defines the durable task programming model. Grain interface projects which declare `DurableTask` methods and clients which call them also reference `Microsoft.Orleans.Durable`, which supplies the code-generation mappings for durable task invocations and the client `AddDurableTasks()` registration.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Durable --prerelease
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)