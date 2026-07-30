---
title: ADO.NET database configuration
description: Learn about ADO.NET database configurations in .NET Orleans.
ms.date: 05/23/2025
ms.topic: how-to
---

# ADO.NET database configuration

The following sections contain links to SQL scripts for configuring your database and the corresponding ADO.NET invariant used to configure ADO.NET providers in Orleans. Customize these scripts as needed for your deployment. Before executing scripts for Clustering, Persistence, or Reminders, you need to create the main tables using the Main scripts.

## Main scripts

| Database | Script | NuGet package| ADO.NET invariant |
|--|--|--|--|
| SQL Server | [SQLServer-Main.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/SQLServer-Main.sql) | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | `System.Data.SqlClient` |
| MySQL / MariaDB | [MySQL-Main.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/MySQL-Main.sql) | [MySql.Data](https://www.nuget.org/packages/MySql.Data/) | `MySql.Data.MySqlClient` |
| PostgreSQL | [PostgreSQL-Main.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/PostgreSQL-Main.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/) | `Npgsql` |
| Oracle | [Oracle-Main.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Shared/Oracle-Main.sql) | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/) | `Oracle.DataAccess.Client` |

## Clustering

| Database | Script | NuGet package| ADO.NET invariant |
|--|--|--|--|
| SQL Server | [SQLServer-Clustering.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql) | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | `System.Data.SqlClient` |
| MySQL / MariaDB | [MySQL-Clustering.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/MySQL-Clustering.sql) | [MySql.Data](https://www.nuget.org/packages/MySql.Data/) | `MySql.Data.MySqlClient` |
| PostgreSQL | [PostgreSQL-Clustering.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/) | `Npgsql` |
| Oracle | [Oracle-Clustering.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Clustering.AdoNet/Oracle-Clustering.sql) | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/) | `Oracle.DataAccess.Client` |

## Persistence

| Database | Script | NuGet package| ADO.NET invariant |
|--|--|--|--|
| SQL Server* | [SQLServer-Persistence.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/SQLServer-Persistence.sql) | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | `System.Data.SqlClient` |
| MySQL / MariaDB | [MySQL-Persistence.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/MySQL-Persistence.sql) | [MySql.Data](https://www.nuget.org/packages/MySql.Data/) | `MySql.Data.MySqlClient` |
| PostgreSQL | [PostgreSQL-Persistence.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/) | `Npgsql` |
| Oracle | [Oracle-Persistence.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Persistence.AdoNet/Oracle-Persistence.sql) | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/) | `Oracle.DataAccess.Client` |

\* If you're using Orleans v3.x use this script template: <https://github.com/dotnet/orleans/blob/3.x/src/AdoNet/Orleans.Persistence.AdoNet/SQLServer-Persistence.sql>

## Reminders

| Database | Script | NuGet package| ADO.NET invariant |
|--|--|--|--|
| SQL Server | [SQLServer-Reminders.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/SQLServer-Reminders.sql) | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | `System.Data.SqlClient` |
| MySQL / MariaDB | [MySQL-Reminders.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/MySQL-Reminders.sql) | [MySql.Data](https://www.nuget.org/packages/MySql.Data/) | `MySql.Data.MySqlClient` |
| PostgreSQL | [PostgreSQL-Reminders.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/PostgreSQL-Reminders.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/) | `Npgsql` |
| Oracle | [Oracle-Reminders.sql](https://github.com/dotnet/orleans/blob/main/src/AdoNet/Orleans.Reminders.AdoNet/Oracle-Reminders.sql) | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/) | `Oracle.DataAccess.Client` |
