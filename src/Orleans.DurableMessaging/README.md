# Microsoft Orleans Durable Messaging

`Microsoft.Orleans.DurableMessaging` adds grain-scoped durable inboxes and outboxes to
Orleans Journaling. Configure the silo after selecting Durable Jobs and Journaling
storage:

```csharp
siloBuilder
    .UseInMemoryDurableJobs()
    .AddDurableMessaging();
```

Inject `IDurableInbox` to register handlers and `IDurableOutbox` to enqueue envelopes.
Inject `IDurableMessagingDiagnostics` to inspect dead letters and remove records after
they have been handled operationally. Removal is staged and becomes durable with the
grain's next journal write.
An outbox enqueue allocates stable job ownership and durably schedules that job before
the grain journal captures the envelope and ownership in one commit. The job safely
polls while the envelope is provisional, and dispatch starts only after the commit. If
the journal write fails, the scheduled job observes no committed envelope and completes
without sending it after activation recovery; before recovery completes, it polls the
same attempt. If recovered work exists without matching ownership, recovery establishes
a new generation before the stale job terminates.
The receiver uses the same schedule-before-commit ordering and returns `Accepted` only
after the inbox envelope and its durable drain-job ownership are stable.

Inbox handlers stage journaled effects and outgoing envelopes, then return. Durable
Messaging commits those effects together with inbox removal and deduplication. Calling
`WriteStateAsync` or `DeleteStateAsync` from a handler is rejected so an early mutation
cannot escape that atomic boundary.

Inbox and outbox dead letters are age- and capacity-bounded by
`DeadLetterRetentionPeriod` and `MaxRetainedDeadLetters`.

Transport is at-least-once and unordered. The receiver deduplicates by
`(SenderId, MessageId)`, providing effectively-once handler effects while the configured
deduplication record is retained. Applications which require ordering must include and
enforce their own sequence numbers.

Durable Messaging requires a Journaling state manager with rollback support and the
`orleans-binary` journal format. Its inbox, outbox, ownership, and durable RPC records
contain Orleans-polymorphic values whose recovery contract is currently validated with
the Orleans serializer. `AddDurableMessaging` selects this format for the host and startup
validation reports an incompatible override before any activation can process messages.
Run workloads which require Journaling's JSON migration path in a separate silo host
which does not call `AddDurableMessaging`.

The state manager must also expose Journaling's request-time mutation guard. Durable
Messaging rejects custom managers without this capability because they cannot prevent
handler-initiated commits or deletes from escaping the inbox completion boundary.

Configure Durable Jobs storage appropriate for the deployment. In-memory storage is for
development and tests only.
