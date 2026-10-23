---
title: Configure ADO.NET providers
description: Configure Orleans clustering, reminders, storage, and grain directories with ADO.NET.
ms.date: 08/23/2026
ms.topic: how-to
---

# Configure ADO.NET providers

Orleans ADO.NET providers use a relational database for one or more runtime capabilities:

| Capability | Package | Configure on |
|---|---|---|
| Clustering | [`Microsoft.Orleans.Clustering.AdoNet`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.AdoNet) | Silos and external clients |
| Grain storage | [`Microsoft.Orleans.Persistence.AdoNet`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AdoNet) | Silos |
| Reminders | [`Microsoft.Orleans.Reminders.AdoNet`](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.AdoNet) | Silos |
| Grain directory | [`Microsoft.Orleans.GrainDirectory.AdoNet`](https://www.nuget.org/packages/Microsoft.Orleans.GrainDirectory.AdoNet) | Silos |
| Streaming | [`Microsoft.Orleans.Streaming.AdoNet`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.AdoNet) | Silos and external clients |

Install only the packages for the capabilities the application uses. Also reference the database driver package.

## Prepare the database

Run the main script for the database first, followed by each capability script. For example, SQL Server clustering and persistence require:

1. `src/AdoNet/Shared/SQLServer-Main.sql`
2. `src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql`
3. `src/AdoNet/Orleans.Persistence.AdoNet/SQLServer-Persistence.sql`

See [ADO.NET database configuration](adonet-configuration.md) for schema script and invariant links.

Apply schema changes as a controlled deployment step. Don't grant silos schema-owner permissions solely so they can create tables at runtime.

The scripts create tables and the `OrleansQuery` table using the database user's
default schema, and the provider queries refer to those objects by unqualified
name. Orleans does not provide an option to select a schema or filegroup. If
your production database requires a dedicated schema, filegroups, partitioning,
or another storage layout, adapt the scripts as part of your database
deployment and keep the resulting table names, columns, parameters, and query
result shapes compatible with Orleans. Apply and test those changes outside the
application startup path.

## Configure with Aspire

`Aspire.Hosting.Orleans` emits the database resource name as `ServiceKey`. The Orleans ADO.NET provider resolves the corresponding connection string and selects the invariant from the Aspire database resource type:

| Aspire resource | Orleans invariant | Supported capabilities |
|---|---|---|
| `SqlServerDatabaseResource`, `AzureSqlDatabaseResource` | `Microsoft.Data.SqlClient` | Clustering, grain storage, reminders, grain directory, streaming |
| `PostgresDatabaseResource`, `AzurePostgresFlexibleServerDatabaseResource` | `Npgsql` | Clustering, grain storage, reminders, grain directory, streaming |
| `MySqlDatabaseResource` | `MySql.Data.MySqlClient` | Clustering, grain storage, reminders, grain directory, streaming |
| `OracleDatabaseResource` | `Oracle.DataAccess.Client` | Clustering, grain storage, reminders |

The ADO.NET AppHost examples keep the silo at explicit start while you prepare its schema. The SQL Server example starts a database resource:

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_apphost":::

PostgreSQL and MySQL/MariaDB use the same schema-preparation sequence:

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_postgresql_apphost":::

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_mysql_apphost":::

Oracle supports clustering, grain storage, and reminders. Its example references a prepared database through the AppHost's `ConnectionStrings:orleans-db` secret or configuration entry:

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_oracle_apphost":::

From the repository root, select an example with:

```powershell
dotnet run --project docs\site\src\content\docs\host\snippets\aspire\AppHost -- --example adonet-sqlserver
```

The other selectors are `adonet-postgresql`, `adonet-mysql`, and `adonet-oracle`. The AppHost project copies the canonical scripts from `src/AdoNet` into `schema` beside the built executable (`AppHost/bin/Debug/net10.0/schema`). Their paths are independent of the process working directory.

For SQL Server, PostgreSQL, and MySQL, wait for the AppHost's `orleans-db` resource to become ready. For Oracle, prepare the database service and application user and configure its connection string before running the AppHost. Use the database's native SQL client to connect to the target database with a schema-owner identity and apply these files in order:

| Example | Ordered files in `schema` |
|---|---|
| SQL Server | `SQLServer-Main.sql`, `SQLServer-Clustering.sql`, `SQLServer-Persistence.sql`, `SQLServer-Reminders.sql`, `SQLServer-Streaming.sql`, `SQLServer-GrainDirectory.sql` |
| PostgreSQL | `PostgreSQL-Main.sql`, `PostgreSQL-Clustering.sql`, `PostgreSQL-Persistence.sql`, `PostgreSQL-Reminders.sql`, `PostgreSQL-Streaming.sql`, `PostgreSQL-GrainDirectory.sql` |
| MySQL/MariaDB | `MySQL-Main.sql`, `MySQL-Clustering.sql`, `MySQL-Persistence.sql`, `MySQL-Reminders.sql`, `MySQL-Streaming.sql`, `MySQL-GrainDirectory.sql` |
| Oracle | `Oracle-Main.sql`, `Oracle-Clustering.sql`, `Oracle-Persistence.sql`, `Oracle-Reminders.sql` |

Use a client which handles the scripts' native batch syntax: `sqlcmd` for SQL Server, `psql` for PostgreSQL, `mysql` for MySQL/MariaDB, and SQL*Plus or SQLcl for Oracle. For Oracle, grant the application user the required schema-creation permissions and execute the scripts as that user; the referenced connection string uses that same user and database service.

Once schema preparation succeeds, start `silo` from the Aspire dashboard. `WaitFor(database)` checks database availability; the explicit start keeps schema preparation ahead of Orleans provider startup. Use this sequence for a new development database and apply [the migration procedure](adonet-configuration.md#apply-and-upgrade-schemas) when retaining an existing database.

### Configure a custom database resource

An `IProviderConfiguration` can emit the `AdoNet` provider type, an explicit invariant, and the connection-string resource name. Custom resources use this configuration to select their database driver:

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_explicit_provider":::

Explicit `Invariant` and `ConnectionString` values take precedence over inferred values. `ServiceKey` resolves an Aspire-injected connection string, and `ConnectionName` resolves a conventional .NET connection string.

When both `ServiceKey` and `ConnectionName` are configured, they must identify the same connection-string entry. Orleans compares the names case-insensitively and reports conflicting references with their provider configuration section.

## Configure SQL Server

Use `Microsoft.Data.SqlClient` for SQL Server:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="adonet_silo":::

Configure an external client with the same clustering database:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="adonet_client":::

> [!IMPORTANT]
> `System.Data.SqlClient` isn't the SQL Server invariant. Reference the `Microsoft.Data.SqlClient` package and use the `Microsoft.Data.SqlClient` invariant.

## Configure another database

The Orleans configuration shape is the same for PostgreSQL, MySQL/MariaDB, and Oracle. Change the driver package, invariant, connection string, and SQL scripts together:

| Database | Driver package | Invariant |
|---|---|---|
| SQL Server | `Microsoft.Data.SqlClient` | `Microsoft.Data.SqlClient` |
| PostgreSQL | `Npgsql` | `Npgsql` |
| MySQL/MariaDB | `MySql.Data` | `MySql.Data.MySqlClient` |
| Oracle | `Oracle.ManagedDataAccess.Core` | `Oracle.DataAccess.Client` |

Orleans also recognizes `MySqlConnector` for the MySqlConnector driver. Verify that the selected capability has a script for the chosen database.

## Use a data source

Each ADO.NET provider option type accepts either a connection string or a <xref:System.Data.Common.DbDataSource>. A data source is useful when the database driver needs configuration which can't be represented in a connection string, such as a periodically refreshed authentication token.

Configure exactly one connection source. Orleans rejects configurations which supply both `ConnectionString` and `DataSource`, or neither. Continue to set `Invariant` because Orleans uses it to select database-specific queries and behavior.

Register the data source as a singleton and resolve it through the provider's `OptionsBuilder` configuration overload. Named Orleans providers can resolve distinct keyed data sources. The dependency injection container or application owns the data source and must keep it alive for the Orleans provider's lifetime; Orleans opens and disposes individual connections but doesn't dispose the data source.

## Configure declaratively

Installed ADO.NET provider packages register `AdoNet` for declarative configuration. For example:

```json
{
  "Orleans": {
    "ServiceId": "orders",
    "ClusterId": "orders-production",
    "Clustering": {
      "ProviderType": "AdoNet",
      "Invariant": "Microsoft.Data.SqlClient",
      "ConnectionString": "..."
    },
    "Reminders": {
      "ProviderType": "AdoNet",
      "Invariant": "Microsoft.Data.SqlClient",
      "ConnectionString": "..."
    },
    "GrainStorage": {
      "Default": {
        "ProviderType": "AdoNet",
        "Invariant": "Microsoft.Data.SqlClient",
        "ConnectionString": "..."
      }
    }
  }
}
```

Store connection strings in a secret provider or deployment environment, not in a committed settings file.

## Operational guidance

- Keep <xref:Orleans.Configuration.ClusterOptions.ServiceId> stable so Orleans reads the expected application rows.
- Size connection pools for the total number of silo and client processes.
- Encrypt connections and use least-privilege database identities.
- Monitor database latency, throttling, deadlocks, and pool exhaustion.
- Test database failover and rolling deployment behavior under load.
- Back up grain state according to application recovery objectives; membership rows are transient and don't replace state backups.

[!INCLUDE [managed-identities](../../../includes/managed-identities.md)]
