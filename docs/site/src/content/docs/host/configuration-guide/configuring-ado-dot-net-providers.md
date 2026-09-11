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

The SQL Server AppHost can create the database and apply the Orleans scripts through a creation script:

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_apphost":::

The `schema/sqlserver.sql` file creates `orleans-db`, selects it, and then contains the SQL Server main script followed by the scripts for each configured capability. Aspire runs this script when the SQL Server resource becomes ready.

PostgreSQL and MySQL/MariaDB container initialization directories can provision the database and schema before the Orleans project starts:

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_postgresql_apphost":::

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_mysql_apphost":::

Place ordered initialization files in the referenced directory. Start with a file that creates `orleans-db`, then apply the main script and each configured capability script. MariaDB uses the MySQL scripts and provider mapping.

Oracle supports clustering, grain storage, and reminders:

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_oracle_apphost":::

The Oracle initialization directory creates the database user or service and applies the Oracle main, clustering, persistence, and reminder scripts. The Oracle container image terms and startup requirements apply to local orchestration.

### Configure a custom database resource

An `IProviderConfiguration` can emit the `AdoNet` provider type, an explicit invariant, and the connection-string resource name. Custom resources use this configuration to select their database driver:

:::code language="csharp" source="../snippets/aspire/AppHost/AppHostExamples.cs" id="adonet_explicit_provider":::

Explicit `Invariant` and `ConnectionString` values take precedence over inferred values. `ServiceKey` resolves an Aspire-injected connection string, and `ConnectionName` resolves a conventional .NET connection string.

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
