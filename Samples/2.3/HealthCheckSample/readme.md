# Orleans Health Checks Sample

Orleans Health Checks sample targeting:

* .NET Core 2.2
* Orleans 2.3.1
* ASP.NET Core 2.2

This sample demonstrates how to integrate [ASP.NET Core Health Checks](https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks) with Orleans for customized health checking.

## TLDR;

* Start the *Silo* project.
* Open http://localhost:8880/health in the browser, or issue a GET with a tool such a Fiddler.
* Check for a response of *Healthy*, *Degraded* or *Unhealthy*.
* Look at the console log update every 30 seconds.

## How It Works

### Silo Host

The *Silo* project hosts both an Orleans silo and a Kestrel web server that serves up Health Check requests.

The Kestrel and Health Check features are implemented as an [`IHostedService`](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services) in [`HealthCheckHostedService`](./src/Silo/HealthCheckHostedService.cs) to facilitate isolation and re-use.

#### Notes

On startup, the silo host selects available ports from a pre-defined range for silo communication, gateway and health checks, in ascending order.
This allows starting multiple silos during development to form a multi-silo cluster for testing.
Pre-defined port ranges are as follows:

|Service|Start|End|
|---|---|---|
| Silo | 11111 | 11119 |
| Gateway | 30000 | 30009 |
| Health | 8880 | 8889 |

You can change these port ranges in [`Program.cs`](./src/Silo/Program.cs).

Please allow the previous silos to come online before starting additional silos.
This avoids transient conflicts in the bare-bones port detection logic.

### Health Checks

During development, make a GET request to http://localhost:8880 to query the first silo host. Replace the port number as appropriate for additional silo hosts.

Under normal operation, the request will return Http Status Code 200 with one of the following strings as content:

* Healthy
* Degraded
* Unhealthy

It can also return Http Status Code 500 (Internal Server Error) in case there is an error running the set of health checks.

Any unreasonable delay in the health check response requires treating as Degraded or Unhealthy by the monitoring tool in use.
The default timeout is 30 seconds.

#### Configuration

Health Checks are configured in [`HealthCheckHostedService`](./src/Silo/HealthCheckHostedService.cs) as per the steps below.

The hosted service requests an instance of [`HealthCheckHostedServiceOptions`](./src/Silo/HealthCheckHostedServiceOptions.cs), which holds these settings as default:

```csharp
public class HealthCheckHostedServiceOptions
{
    public string PathString { get; set; } = "/health";
    public int Port { get; set; } = 8880;
}
```

`.UseKestrel()` tells Kestrel to listen on the given port:

``` csharp
host = new WebHostBuilder()
    .UseKestrel(options => options.ListenAnyIP(myOptions.Value.Port))
```

`.UseHealthChecks()` enables health checking on the given relative path:

``` csharp
.Configure(app =>
{
    app.UseHealthChecks(myOptions.Value.PathString);
})
```

`.AddHealthChecks()` adds infrastructure services and allows adding application health checks:

``` csharp
.ConfigureServices(services =>
{
    services.AddHealthChecks()
        .AddCheck<GrainHealthCheck>("GrainHealth")
        .AddCheck<SiloHealthCheck>("SiloHealth")
        .AddCheck<StorageHealthCheck>("StorageHealth")
        .AddCheck<ClusterHealthCheck>("ClusterHealth");

    services.AddSingleton<IHealthCheckPublisher, LoggingHealthCheckPublisher>()
        .Configure<HealthCheckPublisherOptions>(options =>
        {
            options.Period = TimeSpan.FromSeconds(1);
        });

    /* ... */
})
```

*Health Check* classes derive from [`IHealthCheck`](https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks#create-health-checks).

*Health Check Publisher* classes derive from [`IHealthCheckPublisher`](https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks#health-check-publisher).

#### GrainHealthCheck

The [`GrainHealthCheck`](./src/Silo/GrainHealthCheck.cs) verifies connectivity to a [`LocalHealthCheckGrain`](./src/Grains/LocalHealthCheckGrain.cs) activation.
As this grain is a *Stateless Worker*, validation always occurs in the silo where the health check is issued.

``` csharp
try
{
    await client.GetGrain<ILocalHealthCheckGrain>(Guid.Empty).PingAsync();
}
catch (Exception error)
{
    return HealthCheckResult.Unhealthy("Failed to ping the local health check grain.", error);
}
return HealthCheckResult.Healthy();
```

#### SiloHealthCheck

The [`SiloHealthCheck`](./src/Silo/SiloHealthCheck.cs) verifies if health-checkable Orleans services are healthy.

``` csharp
foreach (var participant in this.participants)
{
    if (!participant.CheckHealth(thisLastCheckTime))
    {
        return Task.FromResult(HealthCheckResult.Degraded());
    }
}
```

Such services implement the [`IHealthCheckParticipant`](../../../src/Orleans.Runtime/Core/IHealthCheckParticipant.cs) interface.

``` csharp
public SiloHealthCheck(IEnumerable<IHealthCheckParticipant> participants)
{
    this.participants = participants;
}
```

For dependency service providers that do not handle discovering services by an arbitrary interface,
we must collect these services ourselves.

At the time of writing this, only [`IMembershipOracle`](../../../src/Orleans.Runtime/MembershipService/IMembershipOracle.cs) exists as a public implementation.

``` csharp
.ConfigureServices(services =>
{
    /* ... */
    services.AddSingleton(Enumerable.AsEnumerable(new IHealthCheckParticipant[] { oracle }));
})
```

#### StorageHealthCheck

The [`StorageHealthCheck`](./src/Silo/StorageHealthCheck.cs) verifies whether the [`StorageHealthCheckGrain`](./src/Grains/StorageHealthCheckGrain.cs) can write, read, and clear state using the default storage provider.

This grain:

* Is marked with [`PreferLocalPlacement`](../../../src/Orleans.Core.Abstractions/Placement/PlacementAttribute.cs);
* Deactivates itself after each call;
* Is called with a random key each time;

This ensures this test always happens in the silo under test.

``` csharp
try
{
    await client.GetGrain<IStorageHealthCheckGrain>(Guid.NewGuid()).CheckAsync();
}
catch (Exception error)
{
    return HealthCheckResult.Unhealthy("Failed to ping the storage health check grain.", error);
}
return HealthCheckResult.Healthy();
```

#### ClusterHealthCheck

The [`ClusterHealthCheck`](./src/Silo/ClusterHealthCheck.cs) verifies whether any silos are unavailable by querying the [`ManagementGrain`](../../../src/Orleans.Runtime/Core/ManagementGrain.cs).

``` csharp
var manager = client.GetGrain<IManagementGrain>(0);
try
{
    var hosts = await manager.GetHosts();
    var count = hosts.Values.Where(_ => _.IsUnavailable()).Count();
    return count > 0 ? HealthCheckResult.Degraded($"{count} silo(s) unavailable") : HealthCheckResult.Healthy();
}
catch (Exception error)
{
    return HealthCheckResult.Unhealthy("Failed to get cluster status", error);
}
```

#### LoggingHealthCheckPublisher

The [`LoggingHealthCheckPublisher`](./src/Silo/LoggingHealthCheckPublisher.cs) reports on the aggregated information from all the health checks.
For simplicity, this publisher reports information to the current logging output.

``` csharp
logger.Log(report.Status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Warning,
    "Service is {@ReportStatus} at {@ReportTime} after {@ElapsedTime}ms with CorrelationId {@CorrelationId}",
    report.Status, now, report.TotalDuration.TotalMilliseconds, id);

foreach (var entry in report.Entries)
{
    logger.Log(entry.Value.Status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Warning,
        entry.Value.Exception,
        "{@HealthCheckName} is {@ReportStatus} after {@ElapsedTime}ms with CorrelationId {@CorrelationId}",
        entry.Key, entry.Value.Status, entry.Value.Duration.TotalMilliseconds, id);
}
```

Reporting startup delay and frequency are configured via the [`HealthCheckPublisherOptions`](https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks#health-check-publisher).

However note that due to [this issue](https://github.com/aspnet/Extensions/issues/1041), the value set for *Period* has no effect at the time of writing,
and the default of 30 seconds will always apply.

``` csharp
.Configure<HealthCheckPublisherOptions>(options =>
{
    options.Period = TimeSpan.FromSeconds(1);
});
```

## Build & Run

On Windows, the easiest way to build and run the sample is to execute the `BuildAndRun.cmd` script.

This will build the project and start a single silo.

Otherwise, use one of the following alternatives...

#### Bash

On Bash-enabled platforms, run the `BuildAndRun.sh` Bash script.

#### PowerShell

On PowerShell-enabled platforms, run the `BuildAndRun.ps1` PowerShell script.

#### Visual Studio

On Visual Studio, configure solution startup to start this project:

* Silo

You can start additional instances by right-clicking on the project and selecting *Debug -> Start new instance*.

#### dotnet

To build and run the sample step-by-step on the command line, use the following commands:

1. `dotnet restore` to restore NuGet packages.
2. `dotnet build --no-restore` to build the solution.
3. `start dotnet run --project ./src/Silo --no-build` to start the first silo.
3. `start dotnet run --project ./src/Silo --no-build` again to start additional silos.
