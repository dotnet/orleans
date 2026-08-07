---
title: ADO.NET grain persistence
description: Configure relational databases, including SQLite, for Orleans grain persistence.
ms.date: 08/02/2026
ms.topic: how-to
---

# ADO.NET grain persistence

The [`Microsoft.Orleans.Persistence.AdoNet`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AdoNet) package stores grain state using database-specific queries exposed through ADO.NET. Orleans includes persistence scripts for:

| Database | Driver package | `Invariant` | Script |
|---|---|---|---|
| SQL Server | `Microsoft.Data.SqlClient` | `Microsoft.Data.SqlClient` | `SQLServer-Persistence.sql` |
| MySQL or MariaDB | `MySql.Data` or `MySqlConnector` | Driver-specific | `MySQL-Persistence.sql` |
| PostgreSQL | `Npgsql` | `Npgsql` | `PostgreSQL-Persistence.sql` |
| Oracle | `Oracle.ManagedDataAccess.Core` | `Oracle.DataAccess.Client` | `Oracle-Persistence.sql` |
| SQLite | `Microsoft.Data.Sqlite` | `System.Data.SQLite` | `Sqlite-Persistence.sql` |

The invariant is an Orleans provider identifier and doesn't always match the driver package name. Install the driver package and run both the shared `*-Main.sql` script and `*-Persistence.sql` script for the selected database.

## Configure a provider

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.AddAdoNetGrainStorage(
        "stateStore",
        options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString =
                builder.Configuration.GetConnectionString("grainState");
        });
});
```

For SQLite:

```csharp
siloBuilder.AddAdoNetGrainStorage(
    "localState",
    options =>
    {
        options.Invariant = "System.Data.SQLite";
        options.ConnectionString = "Data Source=orleans-state.db";
    });
```

SQLite is useful for local, single-process scenarios. Its file locking, deployment topology, and availability characteristics generally don't fit a multi-silo production cluster.

## Serialization and schema evolution

Configure <xref:Orleans.Configuration.AdoNetGrainStorageOptions.GrainStorageSerializer> when the default JSON representation doesn't meet application requirements:

:::code language="csharp" source="./snippets/persistence/StorageConfiguration.cs" id="configure_adonet_serializer":::

Changing the serializer isn't a database migration. The new serializer must read existing payloads or the application must migrate them separately.

## Concurrency contract

ADO.NET persistence scripts implement Orleans' record-level optimistic concurrency. The database version is exposed to application code as an ETag. Writes and clears compare the expected version and fail on a mismatch.

Provider queries must preserve the parameter names, result names, and types expected by Orleans. Persistence writes run inside a database transaction and must roll back on failure. This transaction covers one grain-state record; it doesn't make writes to multiple `IPersistentState<TState>` instances atomic.

## Customize queries

The `OrleansQuery` table contains vendor-specific statements used by the provider. Administrators can tune those statements while preserving the Orleans query contract. Keep customized scripts under source control, apply them through the normal database deployment process—for example, using a [data-tier application (DACPAC)](https://learn.microsoft.com/en-us/sql/tools/sql-database-projects/concepts/data-tier-applications/overview)—and test reads, writes, clears, first-write races, and ETag conflicts after every change.

Database-specific customization can use features such as [partitioned tables and indexes](https://learn.microsoft.com/en-us/sql/relational-databases/partitions/partitioned-tables-and-indexes), [memory-optimized tables](https://learn.microsoft.com/en-us/sql/relational-databases/in-memory-oltp/overview-and-usage-scenarios), [natively compiled modules](https://learn.microsoft.com/en-us/sql/relational-databases/in-memory-oltp/native-compilation-of-tables-and-stored-procedures), [PolyBase](https://learn.microsoft.com/en-us/sql/relational-databases/polybase/overview), or [linked servers](https://learn.microsoft.com/en-us/sql/relational-databases/linked-servers/linked-servers-database-engine) when those capabilities fit the deployment.
