# Streaming: Custom Data Adapter

This sample demonstrates how to use Orleans Streams with a non-Orleans publisher. The external publisher pushes to a stream which is consumed by a grain with the help of a *custom data adapter* which tells Orleans how to interpret stream messages.

## Grain-hosted pulling agents

Run the silo with `--grain-hosted-pulling-agents` to select one directory-registered grain per Event Hubs partition. A central coordinator probes agents every 30 seconds, preserves live placements, and activates missing agents using placement hints. Optional balancing waits one minute and moves only excess agents to equalize partition counts. These intervals are configurable in the silo's pulling-agent options.

During migration, successful source checkpoint persistence precedes destination receiver initialization. Pulling agents also migrate to surviving hosts during graceful silo shutdown.

Use the same flag on every participating silo. To change hosting mode, stop the named provider on all silos and await receiver drain and durable publisher retirement, then switch the configuration and restart from the existing checkpoints. Retry a failed stop before changing mode. Event Hubs retains its inclusive restart boundary, so consumers handle replay.

Explicit pub/sub records each pulling grain's publisher index in the existing named-provider storage, with the `PubSubStore` fallback. The index survives inactive-stream eviction and migration. Budget its writes and record size against the number of distinct streams per partition. Existing deployments of an earlier grain-hosted preview require a separately verified cleanup of legacy unindexed publishers; ordinary old-host stop is insufficient for that upgrade boundary.

The lifecycle regressions exercise in-process migration and retirement. Qualify hard-process-crash recovery for your live Event Hubs, checkpoint store, pub/sub store, and directory combination before production rollout.

This option uses an unreleased Orleans API. Build this sample against the packages produced by the repository's sample build until that API is published.
