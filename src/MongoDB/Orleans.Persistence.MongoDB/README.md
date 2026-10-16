# Microsoft.Orleans.Persistence.MongoDB

MongoDB grain state persistence for Orleans, with optimistic concurrency using
ETags and document-based or binary serialization.

Register a singleton `MongoDB.Driver.IMongoClient`, then call
`AddMongoDBGrainStorage("name", options => options.DatabaseName = "database")`.
Use `AddMongoDBGrainStorageAsDefault` for the default storage provider.

Options are in `Orleans.Persistence.MongoDB`. `ClientName` selects a keyed
`IMongoClient`; leave it unset to use the unkeyed client. Client lifetime is owned
by the application or service container.

`JsonGrainStateSerializer` is the default and stores Newtonsoft.Json state as a
BSON document. `BsonGrainStateSerializer` uses MongoDB's BSON serializer and class
maps to store native, queryable documents. `BinaryGrainStateSerializer` stores
Orleans-serialized bytes. Set `MongoDBGrainStorageOptions.GrainStateSerializer`,
register a keyed `IGrainStateSerializer` for the provider name, or register a
global serializer to select the format.

Collections use the configured `CollectionPrefix` (default `Grains`) followed by
the state name. For the state name `state`, the state type name is used instead.
The default key generator uses the Orleans grain ID string. Keep these values and
the serializer stable when accessing existing data.

Missing reads and successful clears recreate the state through the registered
Orleans activator and reset the ETag and existence metadata.

The JSON serializer supports scalar and array roots. It preserves the legacy
`$`-to-`__` field-name encoding and reserves JSON property names beginning with
`__`; such names fail explicitly before persistence. Cancellation-token storage
overloads propagate cancellation through collection setup and driver operations.

Aspire's MongoDB database resources configure the `MongoDBDatabase` provider.
Register the matching client with `AddKeyedMongoDBClient`; Orleans consumes
`ServiceKey` and derives the database name from Aspire's registered `IMongoDatabase`.

See [MongoDB providers](https://dotnet.github.io/orleans/host/configuration-guide/mongodb-providers)
for format, isolation, and migration guidance.
