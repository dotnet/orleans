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
An outbox enqueue allocates stable job ownership and durably schedules that job before
the grain journal captures the envelope and ownership in one commit. The job safely
polls while the envelope is provisional, and dispatch starts only after the commit. If
the journal write fails, the scheduled job observes no committed envelope and completes
without sending it after activation recovery; before recovery completes, it polls the
same attempt. If recovered work exists without matching ownership, recovery establishes
a new generation before the stale job terminates.
The receiver uses the same schedule-before-commit ordering and returns `Accepted` only
after the inbox envelope and its durable drain-job ownership are stable.

Transport is at-least-once and unordered. The receiver deduplicates by
`(SenderId, MessageId)`, providing effectively-once handler effects while the configured
deduplication record is retained. Applications which require ordering must include and
enforce their own sequence numbers.

Durable Messaging requires a Journaling state manager with rollback support and Durable
Jobs storage appropriate for the deployment. In-memory storage is for development and
tests only.
