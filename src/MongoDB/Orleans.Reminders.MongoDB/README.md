# Microsoft.Orleans.Reminders.MongoDB

MongoDB durable reminder storage for Orleans.

Register a MongoDB client using `UseMongoDBClient`, then call
`UseMongoDBReminders(options => options.DatabaseName = "database")` on the silo
builder. Reminders are partitioned by Orleans `ServiceId` and stored in the
collection `<CollectionPrefix>OrleansReminderV2`.

The provider indexes service IDs and grain hashes for reminder-range queries.
`MongoDBRemindersOptions.CreateIndexes` defaults to `true`. Set it to `false`
when administrators manage indexes or the application has data-only permissions;
administrators should provision `{ ServiceId: 1, GrainHash: 1 }` for range queries.
That setting delegates both index creation and legacy index cleanup to administrators.
Reminder updates generate ETags; removal succeeds when the supplied ETag matches
the stored reminder.

See [MongoDB providers](https://dotnet.github.io/orleans/host/configuration-guide/mongodb-providers)
for configuration, index permissions, and rolling-upgrade guidance.
