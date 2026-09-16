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
An outbox enqueue allocates a logical ownership token and durably schedules a wake-up
carrying that token before the grain journal captures the envelope, ownership token,
and returned job handle in one commit. Durable Jobs assigns each scheduled wake-up its
own physical job ID. Retrying an ambiguous scheduling response can create multiple jobs
for the same logical owner;
the committed handle identifies the drain owner. Repeated callbacks for that job
coalesce, and the other physical jobs complete after the owner is committed.
Each job polls while the envelope is provisional, and dispatch starts after the
commit. After activation recovery, jobs with neither committed ownership nor pending
work complete as orphans. If recovered work exists without matching ownership, recovery
establishes a new generation before the stale job terminates.
The receiver uses the same schedule-before-commit ordering and returns `Accepted` only
after the inbox envelope and its durable drain-job ownership are stable.

Durable Messaging stores its journal entries using the `orleans-binary` journal
format. `AddDurableMessaging` changes the default JSON journal format to
`orleans-binary` and preserves an explicit `orleans-binary` configuration. A different
configured `JournaledStateManagerOptions.JournalFormatKey` produces an
`InvalidOperationException` when the options are evaluated, identifying both the
configured format and the required format.

Transport is at-least-once and unordered. The receiver deduplicates by
`(SenderId, MessageId)`, providing effectively-once handler effects while the configured
deduplication record is retained. Applications which require ordering must include and
enforce their own sequence numbers.

Durable Messaging requires a Journaling state manager with rollback support and Durable
Jobs storage appropriate for the deployment. In-memory storage is for development and
tests only.

Inbox and outbox dead letters are retained for 30 days by default, with up to 1,000
records retained in each collection. Configure `DeadLetterRetentionPeriod` and
`MaxRetainedDeadLetters` through `DurableInboxOptions` to match operational retention
requirements. Expired and excess records are compacted when dead letters are added and
when an activation starts.
