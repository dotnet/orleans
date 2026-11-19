# Microsoft Orleans Durable

## Introduction
Microsoft Orleans Durable provides durable state, jobs, messaging, and tasks for Orleans grains on .NET 11 (`net11.0`). Each feature is registered independently on the silo builder:

| Feature | Registration | Namespace | Guide |
|---|---|---|---|
| Journaling | `AddJournaling()` | `Orleans.Journaling` | [Journaling](https://github.com/dotnet/orleans/blob/main/src/Orleans.Durable/Journaling/README.md) |
| Durable Jobs | `AddDurableJobs()` | `Orleans.DurableJobs` | [Durable Jobs](https://github.com/dotnet/orleans/blob/main/src/Orleans.Durable/DurableJobs/README.md) |
| Messaging | `AddMessaging()` | `Orleans.Messaging` | [Messaging](https://github.com/dotnet/orleans/blob/main/src/Orleans.Durable/Messaging/README.md) |
| Durable Tasks | `AddDurableTasks()` | `Orleans.DurableTasks` | [Durable Tasks](https://github.com/dotnet/orleans/blob/main/src/Orleans.Durable/DurableTasks/README.md) |

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