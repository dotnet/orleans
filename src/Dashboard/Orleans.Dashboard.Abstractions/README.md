# Microsoft Orleans Dashboard Core

## Introduction
Microsoft Orleans Dashboard Core provides the foundational infrastructure and data collection services for the Orleans Dashboard. This package contains the core grain services, metrics collection, and data models used by the dashboard UI.

## Getting Started
This package is typically referenced automatically when you install `Microsoft.Orleans.Dashboard`. You generally don't need to reference this package directly unless you're building custom monitoring solutions or extending the dashboard functionality.

To use this package directly, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Dashboard.Abstractions
```

## What's Included
This package provides:
- **Metrics Collection Services**: Grain-based services that collect runtime statistics
- **Data Models**: Shared types for representing silo and grain statistics
- **History Tracking**: Time-series data storage for performance metrics
- **Grain Profiling**: Method-level performance tracking infrastructure

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans observability](https://learn.microsoft.com/en-us/dotnet/orleans/host/monitoring/)
- [Orleans Dashboard package](https://www.nuget.org/packages/Microsoft.Orleans.Dashboard/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
