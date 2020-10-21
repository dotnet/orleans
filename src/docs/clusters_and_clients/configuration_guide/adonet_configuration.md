# ADO.NET Database Configuration

The following sections contain links to SQL scripts to configure your database as well as the corresponding ADO.NET invariant used to configure ADO.NET providers in Orleans.
These scripts are intended to be customized if needed for your deployment.
Before executing scripts for Clustering, Persistence, or Reminders, one needs to create main tables with the Main scripts.

## Main scripts

| Database        | Script                                                                                                                                       | NuGet Package                                                                  | ADO.NET Invariant             |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------|--------------------------|
| SQL Server      | [SQLServer-Main.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Shared/SQLServer-Main.sql)   | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | System.Data.SqlClient    |
| MySQL / MariaDB | [MySQL-Main.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Shared/MySQL-Main.sql)           | [MySql.Data](https://www.nuget.org/packages/MySql.Data/)                       | MySql.Data.MySqlClient   |
| PostgreSQL      | [PostgreSQL-Main.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Shared/PostgreSQL-Main.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/)                               | Npgsql                   |
| Oracle          | [Oracle-Main.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Shared/Oracle-Main.sql)         | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/)            | Oracle.DataAccess.Client |


## Clustering

| Database        | Script                                                                                                                                       | NuGet Package                                                                  | ADO.NET Invariant             |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------|--------------------------|
| SQL Server      | [SQLServer-Clustering.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql)   | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | System.Data.SqlClient    |
| MySQL / MariaDB | [MySQL-Clustering.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Clustering.AdoNet/MySQL-Clustering.sql)           | [MySql.Data](https://www.nuget.org/packages/MySql.Data/)                       | MySql.Data.MySqlClient   |
| PostgreSQL      | [PostgreSQL-Clustering.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/)                               | Npgsql                   |
| Oracle          | [Oracle-Clustering.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Clustering.AdoNet/Oracle-Clustering.sql)         | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/)            | Oracle.DataAccess.Client |

## Persistence

| Database        | Script                                                                                                                                       | NuGet Package                                                                  | ADO.NET Invariant             |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------|--------------------------|
| SQL Server      | [SQLServer-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/SQLServer-Persistence.sql)   | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | System.Data.SqlClient    |
| MySQL / MariaDB | [MySQL-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/MySQL-Persistence.sql)           | [MySql.Data](https://www.nuget.org/packages/MySql.Data/)                       | MySql.Data.MySqlClient   |
| PostgreSQL      | [PostgreSQL-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/)                               | Npgsql                   |
| Oracle          | [Oracle-Persistence.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Persistence.AdoNet/Oracle-Persistence.sql)         | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/)            | Oracle.DataAccess.Client |

## Reminders

| Database        | Script                                                                                                                                       | NuGet Package                                                                  | ADO.NET Invariant             |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------|--------------------------|
| SQL Server      | [SQLServer-Reminders.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Reminders.AdoNet/SQLServer-Reminders.sql)   | [System.Data.SqlClient](https://www.nuget.org/packages/System.Data.SqlClient/) | System.Data.SqlClient    |
| MySQL / MariaDB | [MySQL-Reminders.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Reminders.AdoNet/MySQL-Reminders.sql)           | [MySql.Data](https://www.nuget.org/packages/MySql.Data/)                       | MySql.Data.MySqlClient   |
| PostgreSQL      | [PostgreSQL-Reminders.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Reminders.AdoNet/PostgreSQL-Reminders.sql) | [Npgsql](https://www.nuget.org/packages/Npgsql/)                               | Npgsql                   |
| Oracle          | [Oracle-Reminders.sql](https://github.com/dotnet/orleans/blob/master/src/AdoNet/Orleans.Reminders.AdoNet/Oracle-Reminders.sql)         | [ODP.net](https://www.nuget.org/packages/Oracle.ManagedDataAccess/)            | Oracle.DataAccess.Client |
