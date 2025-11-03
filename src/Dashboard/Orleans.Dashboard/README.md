# Microsoft Orleans Dashboard

## Introduction
Microsoft Orleans Dashboard is a web-based monitoring and diagnostics tool for Orleans applications. It provides real-time visualization of grain activations, runtime statistics, silo health, and performance metrics through an intuitive web interface.

## Features
- **Real-time Grain Statistics**: Monitor active grain counts, call rates, and exception rates
- **Silo Health Monitoring**: View silo status, CPU usage, memory consumption, and network activity
- **Performance Metrics**: Track method call latencies, throughput, and error rates
- **Grain Method Profiling**: Analyze individual grain method performance
- **Reminders Tracking**: View scheduled reminders across the cluster
- **Dashboard Customization**: Configure authentication, port, and other options

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Dashboard
```

## Example - Adding Dashboard to a Silo

```csharp
using Orleans.Dashboard;

var builder = WebApplication.CreateBuilder(args);

// Configure Orleans
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseLocalhostClustering();
    siloBuilder.UseInMemoryReminderService();
    siloBuilder.AddMemoryGrainStorageAsDefault();

    // Add the dashboard
    siloBuilder.AddDashboard();
});

var app = builder.Build();

// Map dashboard endpoints
app.MapOrleansDashboard();

app.Run();
```

The dashboard will be accessible at the application's base URL (e.g., `http://localhost:5000/`).

## Example - Adding Dashboard to a Separate Web Application

For scenarios where you want to host the dashboard separately from your silos:

```csharp
using Orleans.Dashboard;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Configure Orleans client
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.UseStaticClustering(options =>
        options.Gateways.Add(new IPEndPoint(IPAddress.Loopback, 30000).ToGatewayUri()));

    // Add dashboard services
    clientBuilder.AddOrleansDashboard();
});

var app = builder.Build();

// Map dashboard endpoints
app.MapOrleansDashboard();

await app.RunAsync();
```

## Configuration Options

The dashboard can be configured with various options:

```csharp
siloBuilder.AddDashboard(options =>
{
    options.CounterUpdateIntervalMs = 1000; // Metrics update interval (default: 1000ms)
});
```

You can customize the route prefix when mapping dashboard endpoints:

```csharp
// Map dashboard at a custom path
app.MapOrleansDashboard(routePrefix: "/dashboard");

// Add authentication
app.MapOrleansDashboard().RequireAuthorization();
```

## Dashboard URL
Once configured, the dashboard is accessible at:
- Default: `http://localhost:{AppPort}/` (where AppPort is your web application's port)
- With routePrefix: `http://localhost:{AppPort}/{routePrefix}/`

## Examples
For complete working examples, see the playground projects:
- `playground/DashboardCohosted` - Dashboard cohosted with Orleans in a web application
- `playground/DashboardSeparateHost` - Dashboard in a separate web application connecting to an Orleans cluster

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans observability](https://learn.microsoft.com/en-us/dotnet/orleans/host/monitoring/)
- [Server configuration](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/server-configuration)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
