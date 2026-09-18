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

### Optional membership SQL enhancements

Each silo owns its heartbeat. A heartbeat is one primary-key-targeted assignment to `IAmAliveTime`, sent as a single database command. Heartbeats preserve the logical membership row and table tokens, so a membership update can use tokens captured before a heartbeat. Canonical membership consistency is governed by those logical tokens; heartbeat timestamps are best-effort liveness observations.

Upgrading silos or ADO.NET gateway-discovery clients preserves support for existing query installations. Database updates are optional. With an existing catalog, cleanup continues using its installed `CleanupDefunctSiloEntriesKey` query and behavior.

The existing `*-Clustering.sql` installation scripts include improved conditional-write rollback and Dead-row cleanup. Their optional `CleanupDefunctSiloEntryKey` enables cleanup based on the latest start, heartbeat, and suspicion timestamps, with a captured-row condition protecting concurrent changes. Fresh installations receive these definitions through the usual database setup.

SQL Server joined membership reads use statement snapshots provided by `READ_COMMITTED_SNAPSHOT`, which the existing `SQLServer-Main.sql` setup enables.

Providers load and cache queries during initialization. Existing deployments continue using their installed definitions. Operators choosing to adopt revised definitions can use their normal database change-management process; reinitialize providers afterward to load the revised catalog. The original query keys and parameter sets remain supported.

The SQL enhancements take effect per process as it adopts the updated queries. Until then, cached SQL Server/MySQL updates retain their previous missing-row version-increment behavior, and cached cleanup retains its previous status filter. The enhanced behavior applies cluster-wide once all membership writers use those definitions. SQL Server and MySQL inserts retain the original row-then-version statement order.

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
