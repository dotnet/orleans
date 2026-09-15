# Streaming: Custom Data Adapter

This sample demonstrates how to use Orleans Streams with a non-Orleans publisher. The external publisher pushes to a stream which is consumed by a grain with the help of a *custom data adapter* which tells Orleans how to interpret stream messages.

## Grain-hosted pulling agents

Run the silo with `--grain-hosted-pulling-agents` to select one directory-registered grain per Event Hubs partition. A central coordinator probes agents every 30 seconds, preserves live placements, and activates missing agents using placement hints. Optional balancing waits one minute and moves only excess agents to equalize partition counts. These intervals are configurable in the silo's pulling-agent options.

During migration, successful source checkpoint persistence precedes destination receiver initialization. Pulling agents also migrate to surviving hosts during graceful silo shutdown.

Use the same flag on every participating silo. To change hosting mode, stop and drain the named provider on all silos, switch the configuration, and restart from the existing checkpoints. Event Hubs retains its inclusive restart boundary, so consumers handle replay.

This option uses an unreleased Orleans API. Build this sample against the packages produced by the repository's sample build until that API is published.
