# Microsoft.Orleans.Clustering.MongoDB

MongoDB cluster membership and client gateway discovery for Orleans.

Register a singleton `MongoDB.Driver.IMongoClient`, then call
`UseMongoDBClustering` on both the silo and external client builder. Configure
`DatabaseName`, `CollectionPrefix`, and `Strategy` identically for both, with
matching Orleans `ClusterId` values.

Options are in `Orleans.Clustering.MongoDB`. `ClientName` selects a keyed
`IMongoClient`; leave it unset to use the unkeyed client. All providers can reuse
the same registered client and connection pool.

`SingleDocument` stores each cluster's membership and table version in a single
document. `Multiple` stores individual silo documents and updates the table
version and silo row in a MongoDB transaction; it requires a replica set or a
transaction-capable sharded cluster. `MultipleDeprecated` supports the contributed
provider's legacy collection format.

When migrating `Multiple` or `MultipleDeprecated` from the contributed provider,
stop **all old-provider silos before starting any updated-provider silo**, including
IPv4-only deployments. Older writers can duplicate a new-format member when they
apply membership updates. Use a coordinated stopped-cluster deployment rather than
an overlapping rolling upgrade. Rollback also requires stopping the updated silos
and restoring a pre-migration membership snapshot or using a fresh cluster ID.
Keep application state and reminder collections intact.

Membership reads in `Multiple` use snapshot transactions and an independent session
per operation. Cancellation-aware membership methods propagate cancellation to
the MongoDB commands, cursor reads, and transactions.

The provider supports Aspire's `MongoDB` and `MongoDBDatabase` resource types.
Register the resource client with Aspire's `AddKeyedMongoDBClient`; the Orleans
provider consumes Aspire's `ServiceKey` and registered database name.

See [MongoDB providers](https://dotnet.github.io/orleans/host/configuration-guide/mongodb-providers)
for configuration and operational guidance.
