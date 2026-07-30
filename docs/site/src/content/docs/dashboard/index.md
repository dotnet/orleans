---
title: Orleans Dashboard
description: Learn how to use the Orleans Dashboard for real-time monitoring of your Orleans cluster, silos, and grains.
ms.date: 01/20/2026
ms.topic: concept-article
zone_pivot_groups: orleans-version
---

# Orleans Dashboard

:::zone target="docs" pivot="orleans-10-0"

The Orleans Dashboard is a built-in web-based monitoring tool that provides real-time visibility into your Orleans cluster. It allows you to monitor silo health, grain activations, method calls, reminders, and system metrics without requiring external monitoring infrastructure.

For Orleans 10.0, use the official dashboard packages: `Microsoft.Orleans.Dashboard` and `Microsoft.Orleans.Dashboard.Abstractions`.

[!INCLUDE [orleans-10-preview](../includes/orleans-10-preview.md)]

## Features

The Orleans Dashboard provides the following capabilities:

- **Cluster overview**: View all silos in the cluster with their status, uptime, and resource utilization.
- **Grain monitoring**: Track grain activations, method calls, and performance metrics per grain type.
- **Method profiling**: Analyze grain method call frequency, latency, and error rates.
- **Reminder management**: Browse and monitor all registered reminders across the cluster.
- **Live log streaming**: View real-time log output from the cluster.
- **Silo details**: Inspect individual silo properties, counters, and grain distributions.
- **Grain state inspection**: View the current state of individual grain instances.

## Installation

The Orleans Dashboard is distributed in two NuGet packages:

| Package | Description |
|---------|-------------|
| [Microsoft.Orleans.Dashboard](https://www.nuget.org/packages/Microsoft.Orleans.Dashboard) | Main dashboard package with UI and API endpoints |
| [Microsoft.Orleans.Dashboard.Abstractions](https://www.nuget.org/packages/Microsoft.Orleans.Dashboard.Abstractions) | Abstractions for dashboard integration (e.g., `NoProfilingAttribute`) |

### Basic setup

To add the Orleans Dashboard to your application, call `AddDashboard()` on your silo builder and `MapOrleansDashboard()` to map the dashboard endpoints:

```csharp
using Orleans.Dashboard;

var builder = WebApplication.CreateBuilder(args);

// Configure Orleans with the dashboard
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseLocalhostClustering();
    siloBuilder.AddMemoryGrainStorageAsDefault();
    
    // Add the dashboard services
    siloBuilder.AddDashboard();
});

var app = builder.Build();

// Map dashboard endpoints at the root path
app.MapOrleansDashboard();

app.Run();
```

After starting your application, navigate to `http://localhost:5000/` (or your configured URL) to access the dashboard.

### Custom route prefix

You can host the dashboard at a custom path by specifying a route prefix:

```csharp
// Map dashboard endpoints at /dashboard
app.MapOrleansDashboard(routePrefix: "/dashboard");
```

With this configuration, access the dashboard at `http://localhost:5000/dashboard/`.

## Configuration

### Dashboard options

Configure dashboard behavior using `DashboardOptions`:

```csharp
siloBuilder.AddDashboard(options =>
{
    // Disable the live log streaming endpoint
    options.HideTrace = true;
    
    // Set the counter update interval (minimum 1000ms)
    options.CounterUpdateIntervalMs = 2000;
    
    // Set the history buffer length for metrics
    options.HistoryLength = 200;
});
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `HideTrace` | `bool` | `false` | When `true`, disables the live log streaming endpoint. |
| `CounterUpdateIntervalMs` | `int` | `1000` | Milliseconds between counter samples. Must be >= 1000. |
| `HistoryLength` | `int` | `100` | Number of historical data points to maintain for metrics. |

### Grain profiler options

The grain profiler collects method-level performance data. Configure it using `GrainProfilerOptions`:

```csharp
builder.Services.Configure<GrainProfilerOptions>(options =>
{
    // Always collect profiling data, even when dashboard is inactive
    options.TraceAlways = true;
    
    // Time after which profiling stops if dashboard is inactive
    options.DeactivationTime = TimeSpan.FromMinutes(5);
});
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `TraceAlways` | `bool` | `false` | When `true`, profiling data is continuously collected even when the dashboard is not being queried. |
| `DeactivationTime` | <xref:System.TimeSpan> | 1 minute | Duration of inactivity after which profiling automatically stops. Only applies when `TraceAlways` is `false`. |

### Excluding grains from profiling

Use the `[NoProfiling]` attribute to exclude specific grains from performance profiling:

```csharp
using Orleans.Dashboard;

[NoProfiling]
public class HighFrequencyGrain : Grain, IHighFrequencyGrain
{
    // This grain won't be included in profiling data
}
```

## Deployment patterns

### Co-hosted with silo (recommended)

The simplest deployment pattern is to host the dashboard directly with your Orleans silo. This is the recommended approach for most scenarios:

```csharp
using Orleans.Dashboard;

var builder = WebApplication.CreateBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseLocalhostClustering();
    siloBuilder.UseInMemoryReminderService();
    siloBuilder.AddMemoryGrainStorageAsDefault();
    siloBuilder.AddDashboard();
});

var app = builder.Build();

app.MapOrleansDashboard();

app.Run();
```

### Separate dashboard host

For scenarios where you want to run the dashboard separately from your silos (for example, to provide a dedicated monitoring endpoint), you can host the dashboard on an Orleans client:

```csharp
using Orleans.Configuration;
using Orleans.Dashboard;
using System.Net;

// Start the silo host
var siloHostBuilder = Host.CreateApplicationBuilder(args);
siloHostBuilder.UseOrleans(builder =>
{
    builder.UseDevelopmentClustering(options => 
        options.PrimarySiloEndpoint = new IPEndPoint(IPAddress.Loopback, 11111));
    builder.UseInMemoryReminderService();
    builder.AddMemoryGrainStorageAsDefault();
    builder.ConfigureEndpoints(IPAddress.Loopback, 11111, 30000);
    
    // Dashboard must also be added to silos
    builder.AddDashboard();
});
using var siloHost = siloHostBuilder.Build();
await siloHost.StartAsync();

// Create a separate web application for the dashboard
var dashboardBuilder = WebApplication.CreateBuilder(args);

// Configure Orleans client
dashboardBuilder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.UseStaticClustering(options => 
        options.Gateways.Add(new IPEndPoint(IPAddress.Loopback, 30000).ToGatewayUri()));
    
    // Add dashboard services to the client
    clientBuilder.AddDashboard();
});

var dashboardApp = dashboardBuilder.Build();

// Map dashboard endpoints on the client
dashboardApp.MapOrleansDashboard();

await dashboardApp.RunAsync();

await siloHost.StopAsync();
```

> [!IMPORTANT]
> When using a separate dashboard host, you must still call `AddDashboard()` on the silo builder. The silos need the dashboard services to collect and provide metrics data.

## Authorization

The dashboard endpoints support ASP.NET Core authorization. Use the `RequireAuthorization()` extension method to protect access:

```csharp
// Require authentication for dashboard access
app.MapOrleansDashboard()
   .RequireAuthorization();
```

You can also apply specific authorization policies:

```csharp
// Configure authorization
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("DashboardAccess", policy =>
        policy.RequireRole("Admin", "Operator"));
});

builder.Services.AddAuthentication(/* your auth configuration */);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Apply the policy to dashboard endpoints
app.MapOrleansDashboard()
   .RequireAuthorization("DashboardAccess");
```

## Troubleshooting

### Dashboard shows "lost connectivity" message

This error occurs when the dashboard cannot communicate with the Orleans cluster. Common causes:

1. **Silo not started**: Ensure your Orleans silo is running before accessing the dashboard.
2. **Network issues**: Verify network connectivity between the dashboard host and silos.
3. **Cluster misconfiguration**: Check that clustering is properly configured.

### Profiling data not appearing

If grain method profiling data is empty:

1. **Make grain calls**: Profiling only shows data after grain methods are invoked.
2. **Check `TraceAlways`**: By default, profiling stops after 1 minute of dashboard inactivity. Set `TraceAlways = true` for continuous profiling.
3. **Check `[NoProfiling]`**: Ensure the grain isn't marked with the `[NoProfiling]` attribute.

### Live trace endpoint is disabled

If the `/Trace` endpoint returns 403 Forbidden:

- Check that `DashboardOptions.HideTrace` is not set to `true`.

## See also

- [Observability in Orleans](../host/monitoring/index.md)
- [NuGet packages](../resources/nuget-packages.md)

:::zone-end

:::zone target="docs" pivot="orleans-9-0,orleans-8-0,orleans-7-0"

The Orleans Dashboard is a built-in monitoring tool introduced in Orleans 10.0. For Orleans 9.0, 8.0, and 7.0, use the unofficial community dashboard package:

- **[OrleansDashboard (community)](https://github.com/OrleansContrib/OrleansDashboard)**: A community-maintained dashboard package for Orleans versions earlier than 10.0.
- **OpenTelemetry integration**: Built-in observability features are available in Orleans 7.0 and later. See [Observability in Orleans](../host/monitoring/index.md).

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

The Orleans Dashboard is a built-in monitoring tool introduced in Orleans 10.0. For Orleans 3.x, use the unofficial community dashboard package:

- **[OrleansDashboard (community)](https://github.com/OrleansContrib/OrleansDashboard)**: A community-maintained dashboard package for Orleans versions earlier than 10.0.

:::zone-end
