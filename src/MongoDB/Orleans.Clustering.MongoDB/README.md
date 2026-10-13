# Microsoft.Orleans.Clustering.MongoDB

MongoDB cluster membership and client gateway discovery for Orleans.

Register a MongoDB client using `UseMongoDBClient`, then call
`UseMongoDBClustering` on both the silo and external client builder. Configure
`DatabaseName`, `CollectionPrefix`, and `Strategy` identically for both, with
matching Orleans `ClusterId` values.

`SingleDocument` stores each cluster's membership and table version in a single
document. `Multiple` stores individual silo documents and updates the table
version and silo row in a MongoDB transaction; it requires a replica set or a
transaction-capable sharded cluster. `MultipleDeprecated` supports the contributed
provider's legacy collection format.

Membership reads in `Multiple` use snapshot transactions and an independent session
per operation. Cancellation-aware membership methods propagate cancellation to
the MongoDB commands, cursor reads, and transactions.

See [MongoDB providers](https://dotnet.github.io/orleans/host/configuration-guide/mongodb-providers)
for configuration and operational guidance.
