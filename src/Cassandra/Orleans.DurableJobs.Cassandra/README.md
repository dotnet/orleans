# Microsoft Orleans Durable Jobs for Cassandra

This package persists Orleans Durable Jobs in Cassandra using the Cassandra
journal storage provider. Jobs survive silo restarts and cluster reconfigurations.

Install the core Durable Jobs package and this provider:

```shell
dotnet add package Microsoft.Orleans.DurableJobs
dotnet add package Microsoft.Orleans.DurableJobs.Cassandra
```

Configure a Cassandra connection on the silo:

```csharp
siloBuilder.UseCassandraDurableJobs(options =>
{
    options.ConfigureClient("Contact Points=localhost;Port=9042", keyspace: "orleans");
});
```

`UseCassandraDurableJobs` supports both `ISiloBuilder` and
`IServiceCollection`. It configures Cassandra journal storage and registers
the journal-backed Durable Jobs services and JSON metadata. The provider creates
the journal table and its sharded catalog index at silo startup. The Cassandra
identity needs permission to create both tables, including when tables are
pre-provisioned.

The catalog index uses 32 hash partitions to spread catalog writes. The index
retains journal IDs after deletion so a failed or concurrent create cannot make
a live journal undiscoverable; catalog listing checks each indexed ID against
its current header and does not return deleted journals. Index storage grows
with the number of distinct journal IDs ever registered, and rows are not
reclaimed automatically. By default, the catalog table name is the journal
table name with `_catalog` appended; set `CatalogTableName` to use another
valid CQL identifier.
