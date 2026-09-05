# Orleans Entity Framework Core providers

This directory contains provider-neutral Entity Framework Core implementations and database-specific packages for SQL Server, MySQL/MariaDB, and PostgreSQL.

| Capability | Base package | Database-specific suffixes |
|---|---|---|
| Clustering | `Microsoft.Orleans.Clustering.EntityFrameworkCore` | `.SqlServer`, `.MySql`, `.PostgreSQL` |
| Grain directory | `Microsoft.Orleans.GrainDirectory.EntityFrameworkCore` | `.SqlServer`, `.MySql`, `.PostgreSQL` |
| Persistence | `Microsoft.Orleans.Persistence.EntityFrameworkCore` | `.SqlServer`, `.MySql`, `.PostgreSQL` |
| Reminders | `Microsoft.Orleans.Reminders.EntityFrameworkCore` | `.SqlServer`, `.MySql`, `.PostgreSQL` |

SQL Server uses database-generated `rowversion` ETags. MySQL and PostgreSQL use application-managed `Guid` ETags which are replaced on every insert and update. The ETag mappings must remain concurrency tokens when extending a context.

## Migrations

Database-specific projects own their migrations and idempotent SQL scripts. After changing a model, run these commands from that project directory:

```shell
dotnet ef migrations add <MigrationName> --output-dir Data/Migrations
dotnet ef migrations script --idempotent --output <Capability>.sql
```

Commit the migration, model snapshot, and generated script together. Apply schemas from deployment tooling before starting silos; don't have every silo race to migrate the same database.

## Tests

The tests are in `test/Extensions/Orleans.EntityFrameworkCore.Tests`. Integration tests use:

- `ORLEANSMSSQLCONNECTIONSTRING`
- `ORLEANSMYSQLCONNECTIONSTRING`
- `ORLEANSPOSTGRESCONNECTIONSTRING`

Provider-independent model, registration, and concurrency tests use SQLite and don't require an external database.
