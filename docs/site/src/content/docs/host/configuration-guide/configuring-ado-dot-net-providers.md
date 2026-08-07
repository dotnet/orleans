---
title: Configure ADO.NET providers
description: Configure Orleans clustering, reminders, storage, and grain directories with ADO.NET.
ms.date: 08/02/2026
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

Install only the packages for the capabilities the application uses. Also reference the database driver package.

## Prepare the database

Run the main script for the database first, followed by each capability script. For example, SQL Server clustering and persistence require:

1. `src/AdoNet/Shared/SQLServer-Main.sql`
2. `src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql`
3. `src/AdoNet/Orleans.Persistence.AdoNet/SQLServer-Persistence.sql`

See [ADO.NET database configuration](adonet-configuration.md) for schema script and invariant links.

Apply schema changes as a controlled deployment step. Don't grant silos schema-owner permissions solely so they can create tables at runtime.

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
