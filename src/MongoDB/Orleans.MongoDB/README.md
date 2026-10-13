# Microsoft.Orleans.MongoDB

Shared MongoDB client registration and collection configuration for Orleans
clustering, persistence, and reminder providers. Each provider package brings in
this package automatically.

Register a reusable MongoDB client using `UseMongoDBClient(connectionString)` on
the silo or client builder, or `AddMongoDBClient(connectionString)` on the service
collection. The settings-factory overload accepts
`Func<IServiceProvider, MongoClientSettings>` for credentials, TLS, and connection
pool configuration. Supply connection strings and credentials through application
configuration or a secret store.

The default `IMongoClientFactory` returns the registered singleton `IMongoClient`.
Applications can register their own factory and select a client using each
provider's `ClientName` option.

See [MongoDB providers](https://dotnet.github.io/orleans/host/configuration-guide/mongodb-providers)
for configuration, deployment, and migration guidance.
