# Microsoft Orleans Clustering Provider for ADO.NET

## Introduction
Microsoft Orleans Clustering Provider for ADO.NET allows Orleans silos to organize themselves as a cluster using relational databases through ADO.NET. This provider enables silos to discover each other, maintain cluster membership, and detect and handle failures.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Clustering.AdoNet
```

You will also need to install the appropriate database driver package for your database system:

- SQL Server: `Microsoft.Data.SqlClient`
- MySQL: `MySql.Data` or `MySqlConnector`
- PostgreSQL: `Npgsql`
- Oracle: `Oracle.ManagedDataAccess.Core`
- SQLite: `Microsoft.Data.Sqlite`

## Example - Configuring ADO.NET Clustering

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

// Define a grain interface
public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHello(string greeting);
}

// Implement the grain interface
public class HelloGrain : Grain, IHelloGrain
{
    public Task<string> SayHello(string greeting)
    {
        return Task.FromResult($"Hello, {greeting}!");
    }
}

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            // Configure ADO.NET for clustering
            .UseAdoNetClustering(options =>
            {
                options.Invariant = "System.Data.SqlClient";  // Or other providers like "MySql.Data.MySqlClient", "Npgsql", etc.
                options.ConnectionString = "Server=localhost;Database=OrleansCluster;User Id=myUsername;******;";
            });
    });

var host = builder.Build();
await host.StartAsync();

// Get a reference to a grain and call it
var client = host.Services.GetRequiredService<IClusterClient>();
var grain = client.GetGrain<IHelloGrain>("user123");
var response = await grain.SayHello("World");

// Print the result
Console.WriteLine($"Grain response: {response}");

// Keep the host running until the application is shut down
await host.WaitForShutdownAsync();
```

## Example - Configuring Client to Connect to Cluster

```csharp
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

// Define a grain interface
public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHello(string greeting);
}

var clientBuilder = Host.CreateApplicationBuilder(args)
    .UseOrleansClient(clientBuilder =>
    {
        clientBuilder
            // Configure the client to use ADO.NET for clustering
            .UseAdoNetClustering(options =>
            {
                options.Invariant = "Microsoft.Data.SqlClient";  // Or other providers like "MySql.Data.MySqlClient", "Npgsql", etc.
                options.ConnectionString = "Server=localhost;Database=OrleansCluster;User Id=myUsername;******;";
            });
    });

var host = clientBuilder.Build();
await host.StartAsync();
var client = host.Services.GetRequiredService<IClusterClient>();

// Get a reference to a grain and call it
var grain = client.GetGrain<IHelloGrain>("user123");
var response = await grain.SayHello("World");

// Print the result
Console.WriteLine($"Grain response: {response}");

// Keep the host running until the application is shut down
await host.WaitForShutdownAsync();
```

## Database Setup

Before using the ADO.NET clustering provider, you need to set up the necessary database tables. Scripts for different database systems are available in the Orleans source repository:
namespace ExampleGrains;

- [SQL Server Scripts](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql)
- [MySQL Scripts](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Orleans.Clustering.AdoNet/MySQL-Clustering.sql)
- [PostgreSQL Scripts](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql)
- [Oracle Scripts](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Orleans.Clustering.AdoNet/Oracle-Clustering.sql)

### Upgrading membership queries

Each silo owns its heartbeat. A heartbeat is one primary-key-targeted assignment to `IAmAliveTime`, sent as a single database command. Heartbeats preserve the logical membership row and table tokens, so a membership update can use tokens captured before a heartbeat. Canonical membership consistency is governed by those logical tokens; heartbeat timestamps are best-effort liveness observations.

For an existing database, apply the matching `Migrations/<database>-Clustering-AtomicWrites.sql` update before upgrading **silos or ADO.NET gateway-discovery clients**. The update preserves membership tables and stored rows, updates membership writes, and adds `CleanupDefunctSiloEntryKey` for conditional Dead-row cleanup. Updated providers require this query at initialization and report its absence as an error.

Use a database migration account with permission to update `OrleansQuery` and create or replace the routines in the selected script. Configure the script runner to stop on the first error. Verify the update completed before deploying the provider package.

The MySQL update has two phases, separated by `DELIMITER ;`:

1. Create `InsertMembershipKeyAtomic` once using the intended routine-definer account. The existing `InsertMembershipKey` routine and its permissions continue serving callers which cached the original query.
2. Ensure the runtime database principals have `EXECUTE` permission on the new routine, then execute the catalog-publication transaction following `DELIMITER ;`. Principals with database-wide `EXECUTE` grants already cover the new routine; routine-specific grants must include `InsertMembershipKeyAtomic`. The transaction switches the insert query and publishes the other query updates together.

Providers load and cache queries during initialization. Roll silos and clients after the SQL update so they load the updated catalog; already-running instances continue using their cached queries until restarted. Existing query parameters and membership storage formats support this rolling upgrade. Retain the updated database objects when rolling binaries back: earlier providers can use the updated catalog, and updated providers still require the captured-row cleanup query.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Clustering providers](https://dotnet.github.io/orleans/docs/implementation/cluster-management/)
- [ADO.NET database configuration](https://dotnet.github.io/orleans/docs/host/configuration-guide/adonet-configuration/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
