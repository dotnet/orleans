# Context Map

## Contexts

- [Orleans Journaling](./src/Orleans.Journaling/CONTEXT.md) — persists durable state machine changes as ordered log data.
- [Orleans DurableJobs](./src/Orleans.DurableJobs/CONTEXT.md) — schedules one-time durable work for Orleans grains.

## Relationships

- **Orleans DurableJobs → Orleans Journaling**: DurableJobs stores shard journals in **Log Storage** and uses a **Log Storage Catalog** for shard discovery and ownership coordination.
