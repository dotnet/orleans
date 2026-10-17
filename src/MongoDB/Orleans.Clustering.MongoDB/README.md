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

`Multiple` and `MultipleDeprecated` retain the contributed provider's IDs:
`<ClusterId>@<IPv4-mapped-address>:<port>/<generation>`. Compatible old and updated
writers address the same rows during rolling upgrades. This format requires
distinct mapped address/port/generation combinations within a cluster; native IPv6
addresses with identical low 32 bits can collide. Updated insertions report a
collision explicitly and preserve the stored identity. `SingleDocument` retains
its full-address member keys.

Membership reads in `Multiple` use snapshot transactions and an independent session
per operation. Cancellation-aware membership methods propagate cancellation to
the MongoDB commands, cursor reads, and transactions.

The provider supports Aspire's `MongoDB` and `MongoDBDatabase` resource types.
Register the resource client with Aspire's `AddKeyedMongoDBClient`; the Orleans
provider consumes Aspire's `ServiceKey` and registered database name.

See [MongoDB providers](https://dotnet.github.io/orleans/host/configuration-guide/mongodb-providers)
for configuration and operational guidance.
