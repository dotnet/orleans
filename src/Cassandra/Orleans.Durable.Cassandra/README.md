# Microsoft Orleans Durable Cassandra Providers

## Introduction
Microsoft Orleans Durable Cassandra Providers store journals in Apache Cassandra for `Microsoft.Orleans.Durable`:

- [Journal storage](https://github.com/dotnet/orleans/blob/main/src/Cassandra/Orleans.Durable.Cassandra/Journaling/README.md) for journaled grain state, registered with `AddCassandraJournalStorage`.
- [Durable Jobs storage](https://github.com/dotnet/orleans/blob/main/src/Cassandra/Orleans.Durable.Cassandra/DurableJobs/README.md) for durable job shards, registered with `UseCassandraDurableJobs`.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Durable.Cassandra --prerelease
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