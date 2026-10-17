# Microsoft.Orleans.Reminders.MongoDB

MongoDB durable reminder storage for Orleans.

Register a singleton `MongoDB.Driver.IMongoClient`, then call
`UseMongoDBReminders(options => options.DatabaseName = "database")` on the silo
builder. Reminders are partitioned by Orleans `ServiceId` and stored in the
collection `<CollectionPrefix>OrleansReminderV2`.

Options are in `Orleans.Reminders.MongoDB`. `ClientName` selects a keyed
`IMongoClient`; leave it unset to use the unkeyed client. The application or
service container owns client disposal.

The provider indexes service IDs and grain hashes for reminder-range queries.
`MongoDBRemindersOptions.CreateIndexes` defaults to `true`. Set it to `false`
when administrators manage indexes or the application has data-only permissions;
administrators should provision `{ ServiceId: 1, GrainHash: 1 }` for range queries.
That setting delegates both index creation and legacy index cleanup to administrators.
Reminder updates generate ETags; removal succeeds when the supplied ETag matches
the stored reminder.

IDs retain the contributed provider's `<ServiceId>_<GrainId>_<ReminderName>`
format, supporting old and updated writers during rolling upgrades. Choose
identity combinations with distinct concatenated keys. Updated registrations
report key collisions explicitly, and reads/removals validate the full stored
identity. Concurrent first registrations of the same identity retry a losing
insert as an identity-guarded update. Cancellation tokens reach collection/index
setup and all MongoDB read/write operations.

Aspire's MongoDB database resources configure the `MongoDBDatabase` provider.
Register the resource client with `AddKeyedMongoDBClient`; the provider consumes
Aspire's `ServiceKey` and registered database name.

See [MongoDB providers](https://dotnet.github.io/orleans/host/configuration-guide/mongodb-providers)
for configuration, index permissions, and rolling-upgrade guidance.
