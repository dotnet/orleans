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

New IDs encode each identity component separately. Existing underscore-delimited
IDs are read and updated in place using the full stored service/grain/reminder
identity. Pause new reminder creation during a rolling upgrade until every writer
uses the updated provider. Cancellation tokens reach collection/index setup and
all MongoDB read/write operations.

Aspire's MongoDB database resources configure the `MongoDBDatabase` provider.
Register the resource client with `AddKeyedMongoDBClient`; the provider consumes
Aspire's `ServiceKey` and registered database name.

See [MongoDB providers](https://dotnet.github.io/orleans/host/configuration-guide/mongodb-providers)
for configuration, index permissions, and rolling-upgrade guidance.
