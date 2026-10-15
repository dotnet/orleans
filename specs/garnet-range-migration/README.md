# Garnet range migration specification

[`garnetRangeMigration.qnt`](garnetRangeMigration.qnt) is a [Quint](https://quint-lang.org) specification of bucket handover, lazy migration (pulls and consolidation), tombstones, idempotent retries, and garbage collection for `Orleans.Journaling.Garnet`, as designed in [`src/Garnet/DESIGN.md`](../../src/Garnet/DESIGN.md). It adapts Roux's `specs/range-migration/rangeMigration.qnt` from a node-wide log to per-journal records in Garnet partitions.

## Model

- **Partitions are the actors.** A view maps each bucket to the partition that owns it. Hosting is abstract: a partition is durable and linearizable, and a crashed or unavailable partition takes no steps until it recovers with its state intact.
- **Bucket state is the seal.** Each partition holds each bucket `absent`, `open`, or `sealed`. Every operation checks that its bucket is open in the same transaction, so it commits before a seal or observes it.
- **Journal records** carry their content (a list of appended op ids), an identity (the ETag of the mutation that wrote the record, which never recurs; 0 for no record), and the ids of their last `RECENT` mutations (the recent-ETag list, evicted by count). Tombstones and absent markers read as absent; a tombstone's identity names its absence.
- **Clients** issue conditional writes, conditioned on the identity of a state they observed: the live journal, or its absence (D16). A client may hold any earlier observation (`seen`), which models duplicate activations and old observations. Any reply can be lost, and the client retries the same op at whichever partition it routes to. A retry that finds its id in the history reports success; one that fails its condition reports the outcome as unknown (`ambiguous`). An attempt whose reply was lost may also execute again later, after the client has its outcome (`deliverAttempt`), until it expires (`expire`). An observation of the empty absence includes the bucket's collection generation, which a write conditioned on it carries. Requests carry no view version (D17), so clients may send any operation to any partition.
- **Views and opens.** A view change moves one or more buckets to one partition: a move, a split (some of a partition's buckets), or a merge (buckets of several partitions into one). In `step`, views change by the coordinator rule (`mayMove`), so migrations chain through partitions off the chain and buckets return to earlier owners once their copies are reclaimed. An open is two actions: `authorize` observes the current opener's seal and records an open request for the latest view's migration, at the version which assigned the bucket to its owner, and `openTicket` applies it later at the destination, fenced by the destination's own bucket version (D45). The open carries the sealed source's collection generation, which the destination inherits. Requests may be duplicated (up to `MAX_OPENS` in flight). `uncoordinatedStep` changes views freely.
- **Migration.** A pull (on `NeedPull`) and a consolidation step are two actions, separate requests in the implementation. `fetch` reads a journal from the destination's migration chain, newest first, and records the destination's opening version as the pull's fence; `deliver` installs it later, in one transaction, only if the destination still holds the bucket open at that version with migration incomplete, and holds no record for the journal (D43). `dropPull` discards a pull the destination would reject. Up to `MAX_PULLS` pulls are in flight. Completion is two actions: `prepareDone` enumerates the migration inventory, every journal whose newest record on the chain is live, a tombstone, or an absent marker, as the consolidator lists each link's index and marker set (D42), with the destination's opening version, and `completeDone` sets `done` later, only if the destination is still open at that version and holds a record of every journal in the inventory (D46). Reclamation is also two actions: `authorizeReclaim` authorizes a sealed copy no migration chain reaches (`reachable`, computed from bucket states and stopping at a `done` bucket), bound to the version at which the source sealed the bucket; `reclaimBatch` deletes one batch of journals, fenced on that version (D47). The first batch marks the bucket reclaimed, so reads there fail; the bucket stays sealed. Completions and reclamations may be delayed and duplicated (`MAX_DONES`, `MAX_RECLAIMS` in flight). `dropMarker` removes absent markers and tombstones after `done`, whatever attempts are outstanding. A deletion stamps its tombstone with the bucket copy's collection generation, dropping a tombstone raises the generation past its stamp, and a write conditioned on the empty absence (no record, or an absent marker) fails if the generation has since passed the one it observed (D25).
- **Oracle:** `truth` records each journal's content after every committed write, `applied` and `log` the commits of each op and journal, and `collected` each journal's latest deletion whose tombstone a partition dropped.

## Properties

| Property | Meaning |
| --- | --- |
| `singleOpen` | At most one partition accepts writes for a bucket. |
| `durability` | Reads through the bucket's current opener return every committed write. Deleted and never-written journals read as absent. No read path reaches reclaimed data. |
| `exactlyOnce` | Each op applies at most once. An acknowledged op committed, and an op reported as a conflict did not; an ambiguous one may have committed or not. |
| `retryOutcome` | A retry reports the committed outcome while fewer than `RECENT` later mutations of its journal have committed and no tombstone of a deletion at or after it has been dropped. |
| `migrationsComplete` (temporal) | Under weak fairness of `seal`, `authorize`, `openTicket`, `dropTicket`, `fetch`, `deliver`, `dropPull`, `prepareDone`, `completeDone`, `dropDone`, `authorizeReclaim`, `reclaimBatch`, and `dropReclaim`, every bucket's latest move completes: its owner holds it open and `done`, and every other partition has reclaimed its copy. |

`safety` is the conjunction of the invariants.

## Mutants

Each mutant breaks one rule of the design and must violate `safety`. Random simulation catches each mutant except the four fence mutants, whose counterexamples delay a request across three view changes and a reclamation; their directed scenarios catch them: `unfencedInstallHidesWriteTest` (`mutantUnfencedInstall`), `unfencedOpenTest` (`mutantUnfencedOpen`), `unfencedDoneHidesWriteTest` (`mutantUnfencedDone`), and `unfencedReclaimLosesWriteTest` (`mutantUnfencedReclaim`).

| Mutant | Broken rule |
| --- | --- |
| `mutantOpenBeforeSeal` | Open before the current opener's seal. |
| `mutantUnfencedOpen` | Apply a delayed duplicate open without the destination's version fence, reopening a reclaimed bucket at an old view. |
| `mutantUnconditionalInstall` | A pull overwrites a newer write. |
| `mutantUnfencedInstall` | A pull delivered after its destination reopened the bucket installs a stale copy. |
| `mutantDeleteWithoutTombstone` | Delete during migration without a tombstone, so the source's copy resurrects. |
| `mutantReclaimBeforeDone` | Reclaim a bucket that a read path still reaches. |
| `mutantUnfencedReclaim` | Delete without the source's seal-version fence, so a delayed duplicate deletes a later tenure's records. |
| `mutantMarkDoneEarly` | Mark migration done before every journal is consolidated. |
| `mutantUnfencedDone` | Apply a completion without the destination's opening fence, so a delayed completion checks a stale inventory. |
| `mutantInventoryLiveOnly` | Complete migration against the live index alone, without the tombstones that retried creations rely on. |
| `mutantDropWithoutHorizon` | Drop a tombstone without raising the collection generation, so a retry or delayed attempt of a creation which observed the absence before the deletion applies again. |
| `mutantClientClock` | Order retries against collected tombstones by skewed client clocks, as an earlier design did, so a creator whose clock runs ahead of the deleter's applies its retry again. |
| `mutantRetryWithoutIdempotence` | Retry without recognizing the op's own committed mutation. |
| `mutantRetryAnyAbsence` | A retry conditioned on absence matches any absence instead of the one it observed, so a creation whose id left the history recreates a deleted journal. |
| `mutantWriteAfterSeal` | Acknowledge a write in a sealed bucket (the Delos erratum). |

Directed scenarios ending in `Test` exercise the protocol's main paths (handover, deletes and retries across a migration, chained migrations, merges and splits, returns to an earlier owner, a pull delayed across a reopening, delayed and duplicate opens, and delayed completions and reclamations), the coordinator and reclamation rules, the migration inventory, tombstone collection around queued attempts, the collection generation and client clock skew, old observations, retries within and beyond the history, and one counterexample per mutant.

## Rules the model makes precise

- **Reopen (D11).** A partition other than the current opener opens a bucket only if it holds no copy of it, or has reclaimed its copy, and only at the view which moved the bucket to it (`openAtMovingViewTest`). Otherwise it would serve its stale copy instead of pulling. (The model also lets the current opener reopen a bucket it sealed, keeping its history, after a move that never opened elsewhere; the coordinator rule never produces this.)
- **Coordinator rule (D11).** A view moves bucket `b` to `p` only once the previous move of `b` has opened, and only if `p` holds no copy of `b` or has reclaimed it, which the coordinator observes at `p`; a reclaimed copy is off every read path (`coordinatorRuleWaitsForReclaimTest`). It is a safety prerequisite: an open applies after it is authorized, so without the rule a delayed open at a superseded view leaves two partitions open (`delayedOpenUncoordinatedTest`). It also keeps migrations live: a bucket that returns to a partition whose sealed copy is still on the chain can never open there (`returnToChainedCopyStallsTest`).
- **Open fencing (D45).** An open applies only at a view later than the destination's last transition of the bucket, so a delayed duplicate cannot reopen the bucket after the destination has sealed or reclaimed it (`duplicateOpenFencedTest`).
- **Collection generation (D5, D25).** A tombstone names the absence that retries and attempts are conditioned on and carries their mutation ids. Dropping it, once migration is done, restores the empty absence, so the copy raises its collection generation past the generation stamped on the tombstone at its deletion, and an op conditioned on the empty absence which observed an earlier generation fails. A mutation which committed before a deletion observed its absence at a generation no later than the deletion's stamp, so no retry or delayed attempt applies twice, and collection does not wait for attempts to expire (`collectedTombstoneRetryIsRejectedTest`, `collectWithAttemptRejectedTest`; without the generation, `collectWithAttemptResurrectsTest` and `dropWithoutHorizonAppliesTwiceTest`). The partitions assign generations; ordering by client clocks lets skew through (`mutantClientClock`, `clientClockSkewAppliesTwiceTest`). The generation travels with the bucket to each later opener (`horizonTravelsWithBucketTest`). A client which observed the empty absence before a collection conflicts, observes again, and then creates the journal, which is linearizable because the journal is absent then (`staleAbsenceAfterCollectionTest`). Identities of live journals and tombstones never recur (`staleLiveConditionRejectedTest`). Absent markers carry no ids and are dropped once migration is done.
- **Migration inventory (D42).** Completion covers every journal whose newest record on the chain is live or a tombstone. Deletion removes a journal from the live index, so the consolidator also enumerates retained tombstones (`tombstoneInventoryTest`, `lostCreateDeleteMigrateRetryTest`), and absent markers, which share their set; journals the chain holds no record of read as absent either way (`markDoneNeedsOnlyChainRecordsTest`).
- **Reclamation (D42).** A sealed copy is reclaimable once it is on no migration chain, which holds once a later opener on its chain is done. Holding every journal is not enough (`reclaimWaitsForDoneTest`, `returnWaitsForDoneTest`): the boundary is `done`, which the coordinator observes as the migration's `Done` phase.
- **Completion fencing (D46).** A completion applies only at the opening its inventory was enumerated for (`delayedDoneFencedTest`).
- **Reclamation fencing (D47).** Every deletion batch checks the version at which the source sealed the bucket, so a delayed duplicate cannot delete a later tenure's records (`delayedReclaimFencedTest`). A partition reopens a bucket only once its deletion has finished.
- **Install fencing (D43).** A pull is delivered only at the opening it was fetched for. A destination that reopens a bucket starts empty, so an install conditional only on absence would accept a pull delayed from an earlier opening and hide later writes (`unfencedInstallHidesWriteTest`).
- **Retry history (D44).** The history is bounded by count. A retry whose id has left it fails its condition, which names a state that never recurs, and reports an unknown outcome instead of applying again (`retryAfterEvictionTest`, `retryCreateAfterDeleteTest`).

## Configurations

| Module | Partitions, keys, views, ops, slots for pulls, opens, completions, and reclamations, `RECENT` | Use |
| --- | --- | --- |
| `garnetRangeMigrationSim` | 3, 3 (two buckets), 5, 5, 2 each, 2 | Directed scenarios, simulation, mutants. |
| `garnetRangeMigrationTraces` ([own file](garnetRangeMigrationTraces.qnt)) | as above, `RECENT = 8` (the implementation's) | Traces for the .NET conformance harness. |
| `garnetRangeMigrationTlc` | 3, 2, 3, 2, 1 each, 1 | TLC safety. |
| `garnetRangeMigrationLive` | 3, 2, 3, 1, 1 each, 1 | TLC liveness. |

TLC's translation sends Apalache the IR of every module in the file, and Apalache's server rejects IR over 20 MiB. `quint compile garnetRangeMigration.qnt --main=garnetRangeMigrationTlc --target=json` measures 18,800,559 bytes (17.93 MiB), so later additions have about 2 MiB of headroom; the scenario module accounts for about 7 MiB of it. The trace configuration lives in [`garnetRangeMigrationTraces.qnt`](garnetRangeMigrationTraces.qnt), which imports the model, for that reason: in this file it would add 3.7 MiB.

## Running

```sh
quint typecheck garnetRangeMigration.qnt
quint test garnetRangeMigration.qnt --main=garnetRangeMigrationSim --match=Test
quint run garnetRangeMigration.qnt --main=garnetRangeMigrationSim --backend=rust --invariant=safety --max-steps=40 --max-samples=20000
quint run garnetRangeMigration.qnt --main=garnetRangeMigrationSim --backend=rust --step=mutantOpenBeforeSeal --invariant=safety --max-steps=30 --max-samples=5000 --seed=7
quint verify garnetRangeMigration.qnt --main=garnetRangeMigrationTlc --backend=tlc --invariant=safety
quint verify garnetRangeMigration.qnt --main=garnetRangeMigrationLive --backend=tlc --temporal=migrationsComplete
./generate-traces.sh
```

[`generate-traces.sh`](generate-traces.sh) exports traces with `quint run --mbt --out-itf` from `garnetRangeMigrationTraces` under `step`, and keeps 12 in `test/Extensions/Orleans.Journaling.Garnet.Tests/Conformance/traces`: half raise a collection generation, and the rest exercise completion, reclamation, and rejected pulls, opens, or completions, delayed attempts, or marker collection. With `--mbt`, each state records the action taken (`mbt::actionTaken`) and its nondeterministic arguments (`mbt::nondetPicks`; every action's choices have distinct names), which `GarnetJournalConformanceTests` replays against the real procedures.

On Windows, Quint cannot launch Apalache, which it uses to translate the specification for TLC. Start it first with `apalache-mc.bat server --port=8822` from `%USERPROFILE%\.quint\apalache-dist-<version>\apalache\bin`.

The [workflow](../../.github/workflows/garnet-specs.yml) runs the directed scenarios, a 20,000-trace simulation, and every mutant that simulation catches on each change to this directory. Weekly and on demand, it runs both TLC checks, and checks that `migrationsComplete` fails under `uncoordinatedStep`, so the liveness property tests the coordinator rule.

## Results

Quint 0.33.0, TLC through Apalache 0.62.1, on a shared Azure VM (Intel Xeon Platinum 8370C) that other jobs kept under load, so the durations are upper bounds:

- Directed scenarios: all 56 pass.
- Random simulation (`garnetRangeMigrationSim`: two slots each for pulls, opens, completions, and reclamations, `RECENT = 2`): no violation in 20,000 traces of up to 40 steps (59 seconds).
- Mutants, with 5,000 traces of 30 steps (seed 7): eleven violate `safety` in under a second to 18 seconds (`mutantRetryAnyAbsence` is the slowest). The four fence mutants survive all 5,000; their directed scenarios catch them.
- TLC safety (`garnetRangeMigrationTlc`: one slot of each kind, `RECENT = 1`): exhaustive, no violation; 12,379,961 distinct states (5.57 billion generated), depth 46, 2 hours 34 minutes.
- TLC liveness (`garnetRangeMigrationLive`, `step`): `migrationsComplete` holds over all 465,327 distinct states, depth 42, 5 minutes.
- TLC liveness under `uncoordinatedStep`: `migrationsComplete` is violated within 5,104 distinct states: a bucket returns to a partition whose sealed copy is still on the chain of an opener that the return seals, so the partition can never reopen.
- Conformance: the 12 exported traces replay against the implementation (`GarnetJournalConformanceTests`).

The TLC configurations allow at most three views, so they do not reach the fence mutants' counterexamples, which need four.

## Not yet modeled

- Key filters.
- The MetaStore's migration records and phases; the model derives them from views and bucket states, and the implementation records them from observed bucket states (D27).

Partition failover and fencing are specified separately, in [`garnet-partition-hosting`](../garnet-partition-hosting/README.md), which checks that failover preserves this specification's abstraction of a partition: a durable, linearizable store which takes no steps while it is unavailable.
