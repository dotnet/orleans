# Microsoft Orleans Persistence for Cassandra

Microsoft Orleans Persistence for Cassandra stores grain state in Apache Cassandra.

## Install

```shell
dotnet add package Microsoft.Orleans.Persistence.Cassandra
```

## Configure

```csharp
using Orleans.Hosting;

builder.UseOrleans(silo =>
{
    silo.AddCassandraGrainStorageAsDefault(options =>
    {
        options.ConfigureClient("Contact Points=localhost;Port=9042", "orleans");
        options.CreateTableIfNotExists = true;
    });
});
```

The default provider uses the `grain_state` table when `TableName` is omitted. A named provider must set
`TableName` explicitly. Keyspace and table names must be strict identifiers: they must be non-empty, start with an
ASCII letter, contain only ASCII letters, digits, or underscores, and be at most 48 characters long. The provider
quotes validated identifiers, so names such as `view`, `default`, `unset`, `double`, and `maxwritetime` are supported.
Names are normalized to lowercase inside quoted CQL identifiers to preserve Cassandra's unquoted identifier behavior.

The provider uses the following schema when `CreateTableIfNotExists` is enabled:

```sql
CREATE TABLE grain_state
(
    service_id text,
    grain_id text,
    state_name text,
    grain_type text,
    version bigint,
    record_exists boolean,
    state blob,
    updated_at timestamp,
    PRIMARY KEY ((service_id, grain_id), state_name)
)
```

This schema is compatible with existing deployments using the same column names and types. ETags are decimal
representations of the `version` `bigint`. Wildcard ETags, including `*`, are intentionally not supported.

Table creation is opt-in. The provider can create the table, but it never creates the keyspace. Create the keyspace
before starting the silo, using the replication settings required by your Cassandra deployment.

## Sessions and consistency

`ConfigureClient(string, string)` creates a provider-owned cluster and session. You can instead provide a session
delegate with `ConfigureClient(Func<IServiceProvider, Task<ISession>>)` for an externally owned session. The
provider does not dispose externally owned sessions or clusters. It uses a non-disposing wrapper internally so
provider operations cannot close a shared session. Use the overload with `ownsSession: true` only when the provider
is responsible for the session's cluster lifetime.

The defaults are `Quorum` for ordinary reads and writes and `Serial` for lightweight transactions. These are
conservative correctness-oriented defaults for deployments spanning datacenters. Lower-latency profiles are
explicitly supported by setting `ConsistencyLevel`, including `LocalQuorum`, `One`, and `LocalOne`; `LocalSerial`
is supported as a relaxed serial consistency option. `ConsistencyLevel` must be an ordinary consistency level,
and `SerialConsistencyLevel` must be `Serial` or `LocalSerial`. Select levels which match the replication and
failure model of your Cassandra deployment.

## Clearing state and cancellation

By default, clearing state keeps a tombstone row, sets `record_exists` to `false`, and advances the numeric ETag.
Set `DeleteStateOnClear` to perform a physical delete instead. With no ETag, a clear is allowed when no active row
exists; it cannot safely remove or overwrite an active row and reports a version conflict.

Cancellation stops waiting for the Cassandra operation and is honored before an operation starts. Cassandra driver
requests which have already started do not support cancellation through this provider. The provider tracks those
requests and waits for them during shutdown, and cleans up provider-owned resources after canceled initialization.

## Tests

The provider includes unit tests and Testcontainers-based integration tests. Set `CASSANDRAVERSION` to a
Cassandra container tag to run the integration tests. The tests cover schema creation, existing-schema
compatibility, numeric ETags, lightweight-transaction conflicts, clear behavior, service isolation, and lifecycle
handling.

## Documentation

- [Microsoft Orleans documentation](https://dotnet.github.io/orleans/docs/)
- [Grain persistence](https://dotnet.github.io/orleans/docs/grains/grain-persistence/)
- [Apache Cassandra documentation](https://cassandra.apache.org/doc/)
- [Contributing to Orleans](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)

This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE).
