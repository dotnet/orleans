---
title: ADO.NET database configuration
description: Find Orleans ADO.NET schema scripts and provider invariants.
ms.date: 08/02/2026
ms.topic: reference
---

# ADO.NET database configuration

Orleans keeps its ADO.NET schema scripts beside each provider's source. Run the main script before the capability scripts. Use scripts from the same Orleans release as the packages deployed by the application.

## Driver invariants

| Database | Driver package | Orleans invariant |
|---|---|---|
| SQL Server | [Microsoft.Data.SqlClient](https://www.nuget.org/packages/Microsoft.Data.SqlClient/) | `Microsoft.Data.SqlClient` |
| PostgreSQL | [Npgsql](https://www.nuget.org/packages/Npgsql/) | `Npgsql` |
| MySQL/MariaDB | [MySql.Data](https://www.nuget.org/packages/MySql.Data/) | `MySql.Data.MySqlClient` |
| Oracle | [Oracle.ManagedDataAccess.Core](https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core/) | `Oracle.DataAccess.Client` |

> [!IMPORTANT]
> Use `Microsoft.Data.SqlClient`, not `System.Data.SqlClient`, for SQL Server.

## Main scripts

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/SQLServer-Main.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/PostgreSQL-Main.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/MySQL-Main.sql)
- [Oracle](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/Oracle-Main.sql)
- [SQLite](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/Sqlite-Main.sql) for supported local persistence scenarios

## Clustering

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/MySQL-Clustering.sql)
- [Oracle](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/Oracle-Clustering.sql)

## Persistence

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/SQLServer-Persistence.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/MySQL-Persistence.sql)
- [Oracle](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/Oracle-Persistence.sql)
- [SQLite](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/Sqlite-Persistence.sql)

## Reminders

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/SQLServer-Reminders.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/PostgreSQL-Reminders.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/MySQL-Reminders.sql)
- [Oracle](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/Oracle-Reminders.sql)

## Grain directory scripts

- [SQL Server](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.GrainDirectory.AdoNet/SQLServer-GrainDirectory.sql)
- [PostgreSQL](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.GrainDirectory.AdoNet/PostgreSQL-GrainDirectory.sql)
- [MySQL/MariaDB](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.GrainDirectory.AdoNet/MySQL-GrainDirectory.sql)

Not every capability supports every database. The presence of a script in the provider directory is the authoritative support signal for that Orleans release.

## Apply and upgrade schemas

1. Back up application data according to the database recovery policy.
2. Apply the main script for a new database.
3. Apply the script for each configured Orleans capability.
4. Review and apply scripts under the provider's `Migrations` directory when upgrading from an older schema.
5. Validate with a staging cluster using the same driver and database engine version.

See [Configure ADO.NET providers](configuring-ado-dot-net-providers.md) for host configuration.
