# Microsoft Orleans Clustering Provider for ADO.NET

## Introduction
Microsoft Orleans Clustering Provider for ADO.NET allows Orleans silos to organize themselves as a cluster using relational databases through ADO.NET. This provider enables silos to discover each other, maintain cluster membership, and detect and handle failures.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Clustering.AdoNet
```

You will also need to install the appropriate database driver package for your database system:

- SQL Server: `Microsoft.Data.SqlClient` or `System.Data.SqlClient`
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
                options.Invariant = "System.Data.SqlClient";  // Or other providers like "MySql.Data.MySqlClient", "Npgsql", etc.
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

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Clustering providers](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/cluster-management)
- [Relational Database Provider](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/relational-storage-providers)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)