# Orleans Health Checks Sample

Orleans Health Checks sample targeting .NET Core 2.1.

This sample demonstrates how to integrate the *Microsoft.Extensions.Diagnostics.HealthChecks* and *Microsoft.AspNetCore.Diagnostics.HealthChecks* packages with Orleans for customized health checking.

You can find the source code for the *Health Checks* extension at [Github](https://github.com/aspnet/Extensions).

## TLDR;

* Start the *Silo* project.
* Open http://localhost:8880/health in the browser, or issue a GET with a tool such a Fiddler.
* Check for a response of *Healthy*, *Degraded* or *Unhealthy*.

## How It Works

### Silo Host

The *Silo* project hosts both an Orleans silo and a Kestrel Web Server that serves up Health Check requests.

The Kestrel and Health Check features are implemented as an *IHostedService* in [HealthCheckHostedService](./src/Silo/HealthCheckHostedService.cs) to facilitate isolation and re-use.

#### Notes

On startup, the silo host selects available ports from a pre-defined range for silo communication, gateway and health checks, in ascending order.
This allows starting multiple instances of the silo host during deveopment machine to form a multi-silo cluster for testing.
Pre-defined port ranges are as follows:

|Service|Start|End|
|---|---|---|
| Silo | 11111 | 11119 |
| Gateway | 30000 | 30009 |
| Health | 8880 | 8889 |

You can change this port range as appropriate in [Program.cs](./src/Silo/Program.cs) or remove it altogether for a production deployment.

### Health Checks

During development, make a GET request to http://localhost:8880 to query the first silo host. Replace the port number as appropriate for additional silo hosts.

Under normal operation, the request will return Http Status Code 200 with one of the following strings as content:

* Healthy
* Degraded
* Unhealthy

It can also return Http Status Code 500 (Internal Server Error) in case there is an error running the set of health checks.

Any unreasonable delay in the health check response requires treating as Degraded or Unhealthy by the monitoring tool in use.

#### Configuration

Health Checks are configured in [HealthCheckHostedService](./src/Silo/HealthCheckHostedService.cs) as per the steps below.

The hosted service requests an instance of [HealthCheckHostedServiceOptions](./src/Silo/HealthCheckHostedServiceOptions.cs), which holds these settings as default:

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

`.AddHealthChecks()` adds infrastructure services and allows adding of application health checks.

``` csharp
.ConfigureServices(services =>
{
    services.AddHealthChecks()
        .AddCheck<GrainHealthCheck>("GrainHealth")
        .AddCheck<SiloHealthCheck>("SiloHealth")
        .AddCheck<ClusterHealthCheck>("ClusterHealth");

    /* ... */
})
```

Each health check class must derive from [Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck](https://github.com/aspnet/Extensions/blob/master/src/HealthChecks/Abstractions/src/IHealthCheck.cs).
Health check class instances are transient and run in the order they are added.

#### GrainHealthCheck

The [GrainHealthCheck](./src/Silo/GrainHealthCheck.cs) verifies connectivity to a [LocalHealthCheckGrain](./src/Grains/LocalHealthCheckGrain.cs) activation.
As this grain is a *Stateless Worker*, validation always occurs in the silo where the health check is issued.

``` csharp
public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
{
    try
    {
        await client.GetGrain<ILocalHealthCheckGrain>(Guid.Empty).PingAsync();
    }
    catch (Exception error)
    {
        return HealthCheckResult.Unhealthy("Failed to ping the local health check grain.", error);
    }

    return HealthCheckResult.Healthy();
}
```

#### SiloHealthCheck

The [SiloHealthCheck](./src/Silo/SiloHealthCheck.cs) verifies if health-checkable Orleans services are healthy.

``` csharp
foreach (var participant in this.participants)
{
    if (!participant.CheckHealth(thisLastCheckTime))
    {
        return Task.FromResult(HealthCheckResult.Degraded());
    }
}
```

Such services implement the *Orleans.Runtime.IHealthCheckParticipant* interface.

``` csharp
public SiloHealthCheck(IEnumerable<IHealthCheckParticipant> participants)
{
    this.participants = participants;
}
```

For dependency service providers that do not handle discovering services by an arbitrary interface,
we must collect these services ourselves.

At the time of writing this, only [IMembershipOracle](../../../src/Orleans.Runtime/MembershipService/IMembershipOracle.cs) exists as a public implementation.

``` csharp
.ConfigureServices(services =>
{
    /* ... */
    services.AddSingleton(Enumerable.AsEnumerable(new IHealthCheckParticipant[] { oracle }));
})
```

#### ClusterHealthCheck

The [ClusterHealthCheck](./src/Silo/ClusterHealthCheck.cs) checks whether any silos are unavailable by querying the [ManagementGrain](../../../src/Orleans.Runtime/Core/ManagementGrain.cs).

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

Allow the previous silo to start before starting an additional one.
This avoid conflicts while selecting available ports.