# Microsoft Orleans Persistence for ADO.NET

## Introduction
Microsoft Orleans Persistence for ADO.NET provides grain persistence for Microsoft Orleans using relational databases through ADO.NET. This provider allows your grains to persist their state in various relational databases including SQL Server, MySQL, PostgreSQL, and Oracle.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Persistence.AdoNet
```

You will also need to install the appropriate database driver package for your database system:

- SQL Server: `Microsoft.Data.SqlClient`
- MySQL: `MySql.Data` or `MySqlConnector`
- PostgreSQL: `Npgsql`
- Oracle: `Oracle.ManagedDataAccess.Core`
- SQLite: `Microsoft.Data.Sqlite`

## Example - Configuring ADO.NET Persistence

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure ADO.NET as grain storage
            .AddAdoNetGrainStorage(
                name: "AdoNetStore",
                configureOptions: options =>
                {
                    options.Invariant = "Microsoft.Data.SqlClient";  // Or other providers like "MySql.Data.MySqlClient", "Npgsql", etc.
                    options.ConnectionString = "Server=localhost;Database=OrleansStorage;User Id=myUsername;******;";
                    // Optional: Configure custom queries
                });
    });

// Run the host
await builder.RunAsync();
```

## Example - Using Grain Storage in a Grain

```csharp
using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

// Define grain state class
public class MyGrainState
{
    public string Data { get; set; }
    public int Version { get; set; }
}

// Grain implementation that uses the ADO.NET storage
public class MyGrain : Grain, IMyGrain, IGrainWithStringKey
{
    private readonly IPersistentState<MyGrainState> _state;

    public MyGrain([PersistentState("state", "AdoNetStore")] IPersistentState<MyGrainState> state)
    {
        _state = state;
    }

    public async Task SetData(string data)
    {
        _state.State.Data = data;
        _state.State.Version++;
        await _state.WriteStateAsync();
    }

    public Task<string> GetData()
    {
        return Task.FromResult(_state.State.Data);
    }
}
```

## Database Setup

Before using the ADO.NET provider, you need to set up the necessary database tables. Scripts for different database systems are available in the Orleans source repository:

- [SQL Server Scripts](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Orleans.Persistence.AdoNet/SQLServer-Persistence.sql)
- [MySQL Scripts](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Orleans.Persistence.AdoNet/MySQL-Persistence.sql)
- [PostgreSQL Scripts](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql)
- [Oracle Scripts](https://github.com/dotnet/orleans/tree/main/src/AdoNet/Orleans.Persistence.AdoNet/Oracle-Persistence.sql)
- [SQLite Main Script](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/Sqlite-Main.sql)
- [SQLite Persistence Script](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/Sqlite-Persistence.sql)

The persistence scripts support both setup and repeatable upgrades: reapplying
the current script refreshes persistence queries and routines while preserving
grain state. For a new database, first run the shared Main script for your
database to create the query catalog, then run its persistence script.

Grain state versions cycle through 1 to 2147483647. Each successful write or
clear advances the version, and optimistic concurrency compares ETags for
equality. An ETag repeats after 2147483647 mutations of the same row.
A writer retaining an ETag across that full cycle can match the current version
and overwrite newer state.

To upgrade an existing database, stop all silos using it and reapply the current
persistence script for your database. MySQL recreates its procedures, so reapply
any routine-specific `EXECUTE` grants afterward. Oracle and SQLite also normalize
versions above the integer maximum while preserving their payloads.
Restart the silos after applying the scripts so every provider loads the updated
queries. The wrapping versions fit the integer ETags used by older silos, so
binary rolling upgrades can proceed after this database update.

## Storage Key Format

`AdoNetGrainStorageOptions.KeyFormat` controls how grain state maps to rows.
For new databases, use `GrainTypeQualified`, which gives each combination of
grain type, state name, and grain key its own row:

```csharp
options.KeyFormat = AdoNetGrainStorageKeyFormat.GrainTypeQualified;
```

With `GrainTypeQualified`, grain keys, and grain type and state name combined,
are each limited to about 255 UTF-8 bytes; longer ones fail with an
`ArgumentException`.

The default, `Legacy`, reads and writes state in the format used by earlier
releases. In that format, grains of different types which share a key and state
name share one row. Databases which already hold state keep `Legacy`, because
state written in one format is not visible in the other. `GrainTypeQualified`
cannot be combined with `UseOrleans3CompatibleHasher()`.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Grain Persistence](https://dotnet.github.io/orleans/docs/grains/grain-persistence/)
- [Relational Database Persistence](https://dotnet.github.io/orleans/docs/grains/grain-persistence/relational-storage/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
