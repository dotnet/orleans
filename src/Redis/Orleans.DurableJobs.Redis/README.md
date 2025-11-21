# Microsoft Orleans Durable Jobs for Redis

## Introduction
Microsoft Orleans Durable Jobs for Redis provides persistent storage for Orleans Durable Jobs using Azure Blob Storage. This allows your Orleans applications to schedule jobs that survive silo restarts, grain deactivation, and cluster reconfigurations. Jobs are stored in append blobs, providing efficient storage and retrieval for time-based job scheduling.

## Getting Started

### Installation
To use this package, install it via NuGet along with the core package:

```shell
dotnet add package Microsoft.Orleans.DurableJobs
dotnet add package Microsoft.Orleans.DurableJobs.Redis
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Durable Jobs Core Package](../../../Orleans.DurableJobs/README.md)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
