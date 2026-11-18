# Microsoft Orleans Durable Azure Storage Providers

## Introduction
Microsoft Orleans Durable Azure Storage Providers store journals in Azure Blob Storage or Azure Table Storage for `Microsoft.Orleans.Durable`:

- [Journal storage](https://github.com/dotnet/orleans/blob/main/src/Azure/Orleans.Durable.AzureStorage/Journaling/README.md) for journaled grain state, registered with `AddAzureBlobJournalStorage` or `AddAzureTableJournalStorage`.
- [Durable Jobs storage](https://github.com/dotnet/orleans/blob/main/src/Azure/Orleans.Durable.AzureStorage/DurableJobs/README.md) for durable job shards, registered with `UseAzureBlobDurableJobs` or `UseAzureTableDurableJobs`.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Durable.AzureStorage
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
