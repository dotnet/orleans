# Orleans Dashboard - Separate Host Example

This example demonstrates how to host the Orleans Dashboard in a separate web application that connects to an Orleans cluster as a client.

## Overview

This approach separates the dashboard web service from your silos.

## Key Features

- **Separate Process**: Dashboard web service runs independently from Orleans silos
- **Client Connection**: Connects to the Orleans cluster as an Orleans client
- **Standalone Silo**: Includes a separate silo host for demonstration
- **Test Grains**: Includes test grains to generate activity for the dashboard

## How It Works

The example consists of two components:

1. **Silo Host**: A standalone Orleans silo that runs independently
2. **Dashboard Web App**: A web application that connects as an Orleans client and hosts the dashboard

## Running the Example

```bash
dotnet run
```

The application will:
1. Start an Orleans silo on ports 11111 (silo) and 30000 (gateway)
2. Start the dashboard web application
3. Generate test grain activity automatically

Once running, open your browser to the default ports (typically https://localhost:5001 or http://localhost:5000).

## Code Walkthrough

### Silo Host

```csharp
var siloHost = Host.CreateDefaultBuilder(args)
    .UseOrleans((_, builder) =>
    {
        builder.UseDevelopmentClustering(options =>
            options.PrimarySiloEndpoint = new IPEndPoint(IPAddress.Loopback, 11111));
        builder.ConfigureEndpoints(IPAddress.Loopback, 11111, 30000);
        builder.AddDashboard();
        // ... other configuration
    })
    .Build();
```

### Dashboard Client

```csharp
var dashboardBuilder = WebApplication.CreateBuilder(args);

// Configure Orleans client
dashboardBuilder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.UseStaticClustering(options =>
        options.Gateways.Add(new IPEndPoint(IPAddress.Loopback, 30000).ToGatewayUri()));

    // Add dashboard services
    clientBuilder.AddOrleansDashboard();
});

var app = dashboardBuilder.Build();

// Map dashboard endpoints
app.MapOrleansDashboard();

await app.RunAsync();
```


## Architecture

```
┌─────────────────┐
│  Silo Host      │
│  (Port 11111)   │
│  Gateway: 30000 │
└────────▲────────┘
         │
         │ Orleans Client Connection
         │
┌────────┴────────┐
│  Dashboard      │
│  Web App        │
│  (Port 5000)    │
└─────────────────┘
```

## Customization

### Connect to Remote Cluster

```csharp
dashboardBuilder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.UseStaticClustering(options =>
    {
        options.Gateways.Add(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 30000).ToGatewayUri());
        options.Gateways.Add(new IPEndPoint(IPAddress.Parse("10.0.0.2"), 30000).ToGatewayUri());
    });

    clientBuilder.AddOrleansDashboard();
});
```

### Custom Route Prefix

```csharp
app.MapOrleansDashboard(routePrefix: "/dashboard");
```

### Add Authentication

```csharp
app.MapOrleansDashboard().RequireAuthorization();
```

## Important Notes

> [!WARNING]
> The Orleans Dashboard is designed for **development and testing scenarios only**. It is not recommended for production deployments as it can have a significant performance impact on your cluster.

## Related Examples

- **DashboardCohosted**: Shows how to host the dashboard within a silo
- **DashboardToy**: Advanced example with .NET Aspire integration

## Learn More

- [Orleans Dashboard Documentation](../../../src/Dashboard/Orleans.Dashboard/README.md)
- [Orleans Client Configuration](https://learn.microsoft.com/dotnet/orleans/host/configuration-guide/client-configuration)
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
