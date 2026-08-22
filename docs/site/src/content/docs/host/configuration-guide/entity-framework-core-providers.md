---
title: Entity Framework Core providers
description: Configure Orleans clustering, grain storage, reminders, and grain directories with Entity Framework Core.
ms.date: 08/11/2026
ms.topic: how-to
---

# Entity Framework Core providers

The Orleans Entity Framework Core providers use short-lived, pooled `DbContext` instances for clustering, grain persistence, reminders, and grain directories. They are an alternative to the ADO.NET providers when an application wants EF Core migrations, model customization, and database-provider integration.

## Packages and supported databases

Install one database-specific package for each Orleans capability that the application uses:

| Capability | SQL Server | MySQL or MariaDB | PostgreSQL |
|---|---|---|---|
| Clustering | `Microsoft.Orleans.Clustering.EntityFrameworkCore.SqlServer` | `Microsoft.Orleans.Clustering.EntityFrameworkCore.MySql` | `Microsoft.Orleans.Clustering.EntityFrameworkCore.PostgreSQL` |
| Grain directory | `Microsoft.Orleans.GrainDirectory.EntityFrameworkCore.SqlServer` | `Microsoft.Orleans.GrainDirectory.EntityFrameworkCore.MySql` | `Microsoft.Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL` |
| Grain persistence | `Microsoft.Orleans.Persistence.EntityFrameworkCore.SqlServer` | `Microsoft.Orleans.Persistence.EntityFrameworkCore.MySql` | `Microsoft.Orleans.Persistence.EntityFrameworkCore.PostgreSQL` |
| Reminders | `Microsoft.Orleans.Reminders.EntityFrameworkCore.SqlServer` | `Microsoft.Orleans.Reminders.EntityFrameworkCore.MySql` | `Microsoft.Orleans.Reminders.EntityFrameworkCore.PostgreSQL` |

The SQL Server packages use `Microsoft.EntityFrameworkCore.SqlServer`, the MySQL packages use `Pomelo.EntityFrameworkCore.MySql`, and the PostgreSQL packages use `Npgsql.EntityFrameworkCore.PostgreSQL`.

## Configure the providers

Each package exposes `ISiloBuilder` extensions in the Orleans capability namespace:

| Capability | SQL Server | MySQL | PostgreSQL |
|---|---|---|---|
| Clustering | `UseEntityFrameworkCoreSqlServerClustering` | `UseEntityFrameworkCoreMySqlClustering` | `UseEntityFrameworkCorePostgreSqlClustering` |
| Default grain directory | `UseEntityFrameworkCoreSqlServerGrainDirectoryAsDefault` | `UseEntityFrameworkCoreMySqlGrainDirectoryAsDefault` | `UseEntityFrameworkCorePostgreSqlGrainDirectoryAsDefault` |
| Named grain storage | `AddEntityFrameworkCoreSqlServerGrainStorage` | `AddEntityFrameworkCoreMySqlGrainStorage` | `AddEntityFrameworkCorePostgreSqlGrainStorage` |
| Reminders | `UseEntityFrameworkCoreSqlServerReminderService` | `UseEntityFrameworkCoreMySqlReminderService` | `UseEntityFrameworkCorePostgreSqlReminderService` |

Pass the overload which accepts an `Action<DbContextOptionsBuilder>` and configure the matching EF Core database provider. Clustering must be configured on both silos and external clients. Grain storage, reminders, and grain directories are silo services.

The provider-neutral packages expose generic overloads for applications which supply a custom context derived from the corresponding Orleans context. A custom model can add indexes or constraints, but it must preserve the Orleans keys, relationships, and ETag concurrency token.

## Create and deploy the schema

Each database-specific package contains EF Core migrations and an idempotent SQL script. Apply the schema for every enabled capability before starting the cluster. Run migrations from a deployment job or administration process rather than concurrently from every silo.

Keep generated migrations and scripts under source control. When a custom context changes the model, generate a new migration from the database-specific project:

```dotnetcli
dotnet ef migrations add <MigrationName> --output-dir Data/Migrations
dotnet ef migrations script --idempotent --output Schema.sql
```

Back up production data before applying schema changes. Test both rolling upgrades and rollback against the exact database engine version used in production.

## Concurrency and consistency

The providers implement Orleans optimistic concurrency using the stored ETag. SQL Server uses `rowversion`. MySQL and PostgreSQL use application-managed GUID tokens which change on every insert or update. Treat ETags as opaque values; a stale write fails instead of overwriting a newer record.

Grain persistence stores each grain state as a serialized payload. It doesn't map individual state members to relational columns and doesn't make writes across multiple grains or storage providers atomic. Directly updating provider tables can violate Orleans ownership and consistency guarantees.

## Operational guidance

- Use separate database credentials with only the permissions required by the configured capabilities.
- Protect connection strings with the deployment platform's secret store and require encrypted database connections.
- Size the EF Core context pool and database connection pool for the maximum concurrent silo workload.
- Monitor connection-pool saturation, query latency, deadlocks, concurrency failures, and migration duration.
- Preserve the generated indexes unless load testing demonstrates a safe replacement.
- Test database failover and transient connection failures before production rollout.

The clustering, grain directory, persistence, and reminder providers can use different databases. Select each independently based on durability, latency, operational ownership, and cost.
