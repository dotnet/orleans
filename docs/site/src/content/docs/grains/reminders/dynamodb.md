---
title: Configure Amazon DynamoDB reminders
description: Configure durable Orleans reminder storage with Amazon DynamoDB.
ms.date: 08/28/2026
ms.topic: how-to
---

# Configure Amazon DynamoDB reminders

Install the [`Microsoft.Orleans.Reminders.DynamoDB`](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.DynamoDB) package and call <xref:Orleans.Hosting.DynamoDBSiloBuilderReminderExtensions.UseDynamoDBReminderService*> on every silo.

A silo which uses DynamoDB for both membership and reminders configures the providers independently:

:::code language="csharp" source="../../snippets/compiled/Grains/DynamoDBReminderSnippets.cs" id="configure_dynamodb_reminders":::

<xref:Orleans.Configuration.DynamoDBClusteringOptions> and <xref:Orleans.Configuration.DynamoDBReminderStorageOptions> are separate typed options. Set the AWS region through each options instance, as shown above. A silo which uses another membership provider configures that provider alongside <xref:Orleans.Hosting.DynamoDBSiloBuilderReminderExtensions.UseDynamoDBReminderService*>.

<xref:Orleans.Configuration.ClusterOptions.ServiceId> identifies the application's reminder records and must remain stable across deployments that share a reminder table. Use distinct table names for cluster membership and reminders because each provider manages a different schema.

## Configure AWS credentials

When <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.AccessKey> and <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.SecretKey> remain unset, the [AWS SDK for .NET credential and profile resolution chain](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html) supplies credentials. In production, prefer workload credentials such as an IAM role over long-lived keys. <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.ProfileName>, <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.AccessKey>, <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.SecretKey>, and <xref:Orleans.Reminders.DynamoDB.DynamoDBClientOptions.Token> support deployment environments which require explicit SDK configuration.

## Configure table capacity and lifecycle

The example uses on-demand capacity and an infrastructure-managed table. Set <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.UseProvisionedThroughput> to `true` and configure <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.ReadCapacityUnits> and <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.WriteCapacityUnits> for provisioned capacity.

<xref:Orleans.Configuration.DynamoDBReminderStorageOptions.CreateIfNotExists> and <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.UpdateIfExists> allow the provider to create the reminder table and update its provisioned capacity. Infrastructure-managed provisioning keeps table lifecycle and capacity changes in the deployment workflow.

## Legacy-schema consistency hardening

`TableMode=Legacy` retains the existing table and indexes while hardening reminder scheduling against GSI lag:

- GSI range and grain results are discovery candidates. The provider strongly point-reads every candidate from the base table before returning it, so an obsolete index row cannot resurrect a deleted reminder or restore an old schedule.
- Before a silo removes a known local reminder which a range query omitted, it strongly point-reads that identity. A still-present or newer row is reconciled instead of removed.
- Reminder registration, update, and removal strongly point-read and reconcile before returning when the handling silo remains the owner. A stale handler sends a bounded, three-hop reconciliation notification through current ring views; the receiving owner strongly point-reads before applying it. Per-identity sequencing prevents out-of-order point reads from restoring an older schedule.
- Legacy mode never uses a full-table scan during startup, topology changes, or periodic refresh. All discovery remains cost-bounded to GSI queries and base-table point reads.

This path materially hardens runtime scheduling without changing the key schema, but it cannot provide a complete strong set read. A GSI can omit a row, and a point read can validate only identities already known to the caller. Cold startup and newly acquired ranges therefore cannot cheaply discover an identity which is still absent from the GSI. A mutation notification which exhausts its topology hops, reaches a pre-protocol binary, or encounters a transport failure has the same boundary and is logged. Arbitrary `GetReminders` and range-table calls can also omit completed writes until GSI convergence. Retries do not change this limitation, and Legacy mode does not disguise it with scans. V2 remains required for the full point, grain, and range completed-write visibility guarantee.

Legacy refresh cost is one eventual GSI query for each contiguous owned ring subrange, plus one strongly consistent base-table `GetItem` for each candidate and for each locally known identity omitted by the GSI. A wrap-around range uses two GSI queries. A stale-owner mutation adds at most four system-target messages and, if ownership changes after reads begin, up to four strong point reads. Startup and topology changes use the same bounded operations and consume no scan capacity. Full-table scans occur only inside the explicit V2 migration coordinator, never as the steady-state Legacy correctness mechanism.

## Migrate to the strongly consistent schema

The default <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.TableMode> is `Legacy`, so upgrading Orleans does not change an existing table. The V2 schema stores reminders in a separate table named by <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.V2TableName>, or `${TableName}-v2` when that option is unset. Both names continue to support custom <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.TableName> values.

V2 uses 32 service-scoped hash buckets. Its base-table partition key combines an encoded `ServiceId` with the bucket selected by the unsigned grain hash. Its sort key contains the eight-digit uppercase hexadecimal grain hash followed by tagged, delimiter-safe grain-identity and reminder-name components. Short components use base64url; long components use a SHA-256 digest so the key remains below DynamoDB's 1,024-byte sort-key limit. Writes conditionally verify the complete stored identity, so a digest collision fails instead of overwriting another reminder. These fixed-width prefixes make `(begin, end]`, wrap-around, grain-prefix, and point queries lexicographically exact. Point, grain, and range reads use strongly consistent base-table operations. A range refresh issues bounded key queries against the 32 buckets instead of scanning the table.

### Prerequisites

- Enable point-in-time recovery or take an on-demand backup of the legacy table.
- Grant `CreateTable`, `DescribeTable`, `Scan`, `Query`, `GetItem`, `PutItem`, `DeleteItem`, and `TransactWriteItems` permissions for both table names. Infrastructure-managed deployments must provision the V2 table with string `PartitionKey` and string `SortKey` base keys before migration and set `CreateIfNotExists` and `UpdateIfExists` accordingly.
- Size V2 write capacity for the temporary dual-write period. Migration uses strongly consistent legacy scans and transactional copy operations. Set <xref:Orleans.Configuration.DynamoDBReminderStorageOptions.MigrationPageSize> to bound each resumable scan page; a smaller value reduces bursts but increases requests.
- Keep DynamoDB transactions available. The two tables must remain in the same AWS account and Region.

### Rolling protocol

1. Deploy the V2-capable binary to every silo with `TableMode=Migrate`. Reads remain on V1. Each completed registration and removal writes V1 and V2 atomically with the same ETag. A leased coordinator strongly scans V1, conditionally copies each row, removes V2 rows whose V1 source is still absent, and verifies both schemas. Persisted page keys make interruption and lease takeover resumable and idempotent.
2. Wait for every silo to run the new binary and for the migration log to report `Ready`. Keep this stage for at least the longest expected old deployment shutdown and AWS SDK retry interval.
3. Change every silo to `TableMode=V2`. Finalization fails closed if any active Orleans membership entry lacks a fresh V2-capability marker. It repeats reconciliation and exact verification, confirms membership did not change, and only then persists `Cutover`. V2-capable silos consult this strongly consistent state before reads, so already-running `Migrate` silos follow the cutover while the configuration rollout completes.
4. Keep `TableMode=V2` and retain V1 for the desired rollback window. Dual writes continue after cutover. Do not run a binary which predates this protocol after cutover: old binaries do not understand the fence and can write only V1. The capability check covers active silos at transition time, not a future operator-initiated downgrade.

This is a two-phase deployment, not a one-step rolling cutover. A direct `Legacy` to `V2` full-cluster restart is also safe when all old processes are stopped before the first V2 silo starts. Finalization never uses a GSI or a periodic full-table scan.

### Roll back and retire V1

Set every V2-capable silo to `TableMode=Rollback`. The coordinator requires compatible active silos, reconciles and verifies the transactionally maintained copies, then persists `RolledBack`; all V2-capable silos return to V1 reads. After correcting the issue, repeat `Migrate` and `V2`.

The provider never deletes or disables the legacy table. After the rollback window, set every silo to `TableMode=V2Only`. The coordinator performs one last fenced verification with stable compatible membership and persists the irreversible `Retired` state. Already-running V2-capable silos observe that state before each mutation and stop writing V1. After every silo reports `Retired`, archive or delete V1 through the deployment system. `V2Only` startup does not recreate V1, and rollback is no longer available.

### States, recovery, and operations

Migration metadata is isolated by `ServiceId` in V2. `Backfilling` stores the last evaluated V1 key after each completed page; replay after a crash is harmless because the source ETag is checked transactionally. `Verifying` performs final reconciliation. `Ready` means exact source and target counts and contents matched. `Cutover` selects V2 reads. `RolledBack` selects V1 reads. `VerificationFailed` preserves V1 reads and prevents cutover. `Retired` selects V2-only reads and writes and is irreversible.

Only one unexpired migration lease can advance a service. Other `Migrate` silos continue serving V1 and dual-writing. A stopped owner is recoverable after lease expiry; restarting any configured silo resumes from the checkpoint. Conditional conflicts caused by an old V1 writer replay the same page instead of advancing past the change. A V1 delete cannot resurrect a stale V2 row because copy and source-ETag validation are one transaction.

Orleans logs state transitions, page numbers and matching item counts, lease contention, and verification failures. Monitor those logs together with DynamoDB `ConsumedReadCapacityUnits`, `ConsumedWriteCapacityUnits`, `ReadThrottleEvents`, `WriteThrottleEvents`, `TransactionConflict`, and `SystemErrors` metrics for both tables. `VerificationFailed`, repeated lease loss, or an incompatible-silo error requires operator action and never changes the read schema.

Backfill scans are migration-only and strongly consistent. Because a filtered DynamoDB scan consumes capacity for every evaluated item, migrating one `ServiceId` in a large shared V1 table reads the full physical table. Schedule large migrations around capacity limits, reduce `MigrationPageSize`, and migrate services separately. This protocol does not depend on DynamoDB Streams retention; interruption recovery uses persisted scan keys plus a final full reconciliation. If an external change-capture process is used operationally, its stream-retention window does not replace the final verification.
