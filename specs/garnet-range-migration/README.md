# Garnet range migration specification

[`garnetRangeMigration.qnt`](garnetRangeMigration.qnt) is a [Quint](https://quint-lang.org) specification of bucket handover, lazy migration (pulls and consolidation), tombstones, idempotent retries, and garbage collection for `Orleans.Journaling.Garnet`, as designed in [`src/Garnet/DESIGN.md`](../../src/Garnet/DESIGN.md). It adapts Roux's `specs/range-migration/rangeMigration.qnt` from a node-wide log to per-journal records in Garnet partitions.

## Model

- **Partitions are the actors.** A view maps each bucket to the partition that owns it. Hosting is abstract: a partition is durable and linearizable, and a crashed or unavailable partition takes no steps until it recovers with its state intact.
- **Bucket state is the seal.** Each partition holds each bucket `absent`, `open`, or `sealed`. Every operation checks that its bucket is open in the same transaction, so it commits before a seal or observes it.
- **Journal records** carry their content (a list of appended op ids), a version (the content ETag), and the ids of the mutations applied to them (the recent-ETag list). Tombstones and absent markers read as absent.
- **Clients** issue conditional writes. A client conditions on the version it observed, or on the journal's absence; a stale condition models a duplicate activation. Any reply can be lost, and the client retries the same op at whichever partition it routes to. Requests carry no view version (D17), so clients may send any operation to any partition.
- **Views.** A view change moves one or more buckets to one partition: a move, a split (some of a partition's buckets), or a merge (buckets of several partitions into one). In `step`, views change freely, so migrations chain and buckets return to earlier owners. `coordinatedStep` changes views only by the coordinator rule (`mayMove`).
- **Migration.** `install` is both a pull (on `NeedPull`) and a consolidation step: it copies a journal from the bucket's migration chain, newest first, only if the destination holds no record for it. `markDone` completes migration once the destination holds every journal of the bucket. `reclaim` deletes a sealed bucket's journals once no read path reaches them; the bucket stays sealed and reads there fail. `dropMarker` removes absent markers after `done`, and tombstones after `done` once no retry on the journal can arrive.
- **Oracle:** `truth` records each journal's content after every committed write.

## Properties

| Property | Meaning |
| --- | --- |
| `singleOpen` | At most one partition accepts writes for a bucket. |
| `durability` | Reads through the bucket's current opener return every committed write. Deleted and never-written journals read as absent. No read path reaches reclaimed data. |
| `exactlyOnce` | Each op applies at most once. An acknowledged op committed, and an op reported as a conflict did not. |
| `migrationsComplete` (temporal) | Under weak fairness of `seal`, `open`, `install`, `markDone`, and `reclaim`, every bucket's latest move completes: its owner holds it open and `done`, and every other partition has reclaimed its copy. Holds with `coordinatedStep`. |

`safety` is the conjunction of the invariants.

## Mutants

Each mutant breaks one rule of the design and must violate `safety`:

| Mutant | Broken rule |
| --- | --- |
| `mutantOpenBeforeSeal` | Open before the current opener's seal. |
| `mutantUnconditionalInstall` | A pull overwrites a newer write. |
| `mutantDeleteWithoutTombstone` | Delete during migration without a tombstone, so the source's copy resurrects. |
| `mutantReclaimBeforeDone` | Reclaim a bucket that a read path still reaches. |
| `mutantMarkDoneEarly` | Mark migration done before every journal is consolidated. |
| `mutantRetryWithoutIdempotence` | Retry without recognizing the op's own committed mutation. |
| `mutantWriteAfterSeal` | Acknowledge a write in a sealed bucket (the Delos erratum). |

Directed scenarios ending in `Test` exercise the protocol's main paths (handover, deletes and retries across a migration, chained migrations, merges and splits, and returns to an earlier owner), the coordinator rule, and one counterexample per mutant.

## Rules the model makes precise

- **Reopen (D11).** A partition other than the current opener opens a bucket only if it holds no copy of it, or has reclaimed its copy. Otherwise it would serve its stale copy instead of pulling. (The model also lets the current opener reopen a bucket it sealed, keeping its history, after a move that never opened elsewhere; the coordinator rule never produces this.)
- **Coordinator rule (D11).** A view moves bucket `b` to `p` only once the previous move of `b` has opened, and only if no read path reaches `p`'s copy of `b`. Without it, safety still holds but migrations can stall: a bucket that returns to a partition whose sealed copy is still on the chain can never open there (`returnToChainedCopyStallsTest`), and the same happens when a partition opens at a superseded view.
- **Tombstone retention (D5).** A tombstone carries the mutation ids that retries rely on, so it is dropped only after migration is done and once no retry of an op on that journal can still arrive. Absent markers carry none and are dropped once migration is done.
- **Reclamation (D26).** A sealed copy is reclaimable once the migration walk from the bucket's current opener no longer visits it, which holds once a later opener on its chain is done.

## Running

```sh
quint typecheck garnetRangeMigration.qnt
quint test garnetRangeMigration.qnt --main=garnetRangeMigrationSim --match=Test
quint run garnetRangeMigration.qnt --main=garnetRangeMigrationSim --backend=rust --invariant=safety --max-steps=40 --max-samples=20000
quint run garnetRangeMigration.qnt --main=garnetRangeMigrationSim --backend=rust --step=mutantOpenBeforeSeal --invariant=safety --max-steps=30 --max-samples=5000 --seed=7
quint verify garnetRangeMigration.qnt --main=garnetRangeMigrationTlc --backend=tlc --invariant=safety
quint verify garnetRangeMigration.qnt --main=garnetRangeMigrationLive --backend=tlc --step=coordinatedStep --temporal=migrationsComplete
quint run garnetRangeMigration.qnt --main=garnetRangeMigrationSim --mbt --max-steps=30 --max-samples=50 --n-traces=50 --out-itf=traces/trace_{seq}.itf.json
```

The last command exports traces for the .NET conformance harness into an existing `traces` directory. With `--mbt`, each state records the action taken (`mbt::actionTaken`) and its nondeterministic arguments (`mbt::nondetPicks`), which the harness replays against the real procedures.

On Windows, Quint cannot launch Apalache, which it uses to translate the specification for TLC. Start it first with `apalache-mc.bat server --port=8822` from `%USERPROFILE%\.quint\apalache-dist-<version>\apalache\bin`.

The [workflow](../../.github/workflows/garnet-specs.yml) runs the directed scenarios, a 20,000-trace simulation, and every mutant on each change to this directory. Weekly and on demand, it runs both TLC checks, and checks that `migrationsComplete` fails under `step`, so the liveness property tests the coordinator rule.

## Results

Quint 0.33.0, TLC through Apalache 0.62.1, on a shared Azure VM (Intel Xeon Platinum 8370C):

- Directed scenarios: all 20 pass.
- Random simulation (`garnetRangeMigrationSim`): no violation in 100,000 traces of up to 40 steps (3 minutes 15 seconds).
- Mutants: each violates `safety` within 5,000 traces of 30 steps (seed 7), in 2–7 seconds.
- TLC safety (`garnetRangeMigrationTlc`): exhaustive, no violation; 1,515,186 distinct states (303 million generated), depth 27, 8 minutes 23 seconds.
- TLC liveness (`garnetRangeMigrationLive`, `coordinatedStep`): `migrationsComplete` holds over all 119,370 distinct states, depth 26, 46 seconds.
- TLC liveness under `step`: `migrationsComplete` is violated in 4 seconds. In the counterexample, bucket 1 moves 2 → 3 → 2; partition 3 opens at the superseded view, so partition 2's sealed copy stays on the chain of an opener that is itself sealed, and partition 2 can never reopen.

## Not yet modeled

- Key filters.
- Partition failover and epoch fencing (phase 4).
- The MetaStore's migration records and phases; the model derives them from views and bucket states.
