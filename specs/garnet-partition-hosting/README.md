# Garnet partition hosting specification

[`partitionHosting.qnt`](partitionHosting.qnt) is a [Quint](https://quint-lang.org) specification of partition hosting and failover on shared blob storage, with fencing, for `Orleans.Journaling.Garnet`, as designed in [`src/Garnet/DESIGN.md`](../../src/Garnet/DESIGN.md) (decision D12). It complements [`garnet-range-migration`](../garnet-range-migration/README.md), which treats each partition as a durable, linearizable store that is sometimes unavailable. This specification checks that failover preserves that abstraction while previous hosts keep running as zombies.

## Model

- **Storage.** A partition's state is a set of page blobs, here AOF segments holding the ops flushed to them, and a manifest blob which registers the segments recovery reads. Every blob and the manifest have a lease. Each storage request is one atomic step, as the storage service executes it.
- **Tenures.** A tenure is one host's hold on the partition at a hosting epoch, which is its id. The coordinator assigns the partition to the next tenure at any time (`reassign`), whether or not the current host is dead. A tenure knows only its own state: its in-memory store, its unflushed ops, its current segment, and a read in progress.
- **Takeover** (`takeOver`, `fence`, `recover`): break and acquire the manifest's lease, check that no later epoch holds the storage and record this epoch, then break and acquire the lease of every existing blob, then confirm by a lease-conditioned manifest update that the tenure still holds the manifest, and recover the registered segments.
- **Serving.** A write is applied in memory and acknowledged once a flush writes it to the current segment under the tenure's lease. A read observes memory and logs a record, so it replies only after a flush. A failed flush fails the tenure, which then neither acknowledges nor answers anything, as Garnet does once its AOF commit fails.
- **Segments** (`create`, `acquire`, `register`): create a segment if absent (or replace one this tenure fenced), acquire its lease, then register it under the manifest's lease before appending to it. Only a takeover breaks leases.

## Properties

| Property | Meaning |
| --- | --- |
| `recoverableAcked` | Every acknowledged write is in a registered segment, so the next takeover recovers it. |
| `holderHasAcked` | The serving tenure which holds the manifest's lease has every acknowledged write. |
| `readsLinearizable` | Every read returned every write acknowledged before it began, and only acknowledged writes. |

`safety` is their conjunction.

## Mutants

Each mutant breaks one rule of the protocol and must violate `safety`:

| Mutant | Broken rule |
| --- | --- |
| `mutantUnfencedWrite` | A flush ignores the blob's lease, so a zombie acknowledges a write the new tenure never recovered. |
| `mutantUnfencedRead` | A read replies without logging a record, so a zombie returns a state missing writes the new tenure acknowledged. |
| `mutantUnfencedManifest` | A takeover does not acquire the manifest's lease, so a zombie registers a segment and acknowledges writes in it. |
| `mutantUnregisteredWrite` | A tenure writes to a segment before registering it, so recovery does not read the acknowledged write. |
| `mutantCreateBreaksLease` | Segment creation breaks another tenure's lease to replace an existing segment, destroying acknowledged writes. |
| `mutantUnconfirmedRecovery` | A takeover recovers without confirming the manifest's lease, so a slower, earlier takeover breaks the leases of a later one which has already recovered, and then serves. |
| `mutantRecoverBeforeFence` | A takeover recovers before fencing every blob, so a zombie's later flush is acknowledged but not recovered. |

Random simulation finds every violation except `mutantUnconfirmedRecovery`'s, which needs two takeovers to interleave; the directed scenario `unconfirmedRecoveryTest` exhibits it, and TLC finds it. TLC found that rule: an earlier version of the protocol recovered without confirming.

## Configurations

| Module | Tenures, segments, ops | Use |
| --- | --- | --- |
| `partitionHostingSim` | 3, 3, 3 | Directed scenarios, simulation, mutants. |
| `partitionHostingTlc` | 3, 2, 2 | TLC safety. |

## Running

```sh
quint typecheck partitionHosting.qnt
quint test partitionHosting.qnt --main=partitionHostingSim --match=Test
quint run partitionHosting.qnt --main=partitionHostingSim --backend=rust --invariant=safety --max-steps=40 --max-samples=20000
quint run partitionHosting.qnt --main=partitionHostingSim --backend=rust --step=mutantUnfencedWrite --invariant=safety --max-steps=30 --max-samples=10000 --seed=7
quint verify partitionHosting.qnt --main=partitionHostingTlc --backend=tlc --invariant=safety
```

On Windows, start Apalache first, as described in the [range migration specification's README](../garnet-range-migration/README.md#running).

## Results

Quint 0.33.0, TLC through Apalache 0.62.1, on a shared Azure VM (Intel Xeon Platinum 8370C):

- Directed scenarios: all 15 pass.
- Random simulation (`partitionHostingSim`): no violation in 20,000 traces of up to 40 steps (6 seconds).
- Mutants: each random-simulation mutant violates `safety` within 10,000 traces of 30 steps (seed 7), in 2–7 seconds; `unconfirmedRecoveryTest` exhibits `mutantUnconfirmedRecovery`.
- TLC safety (`partitionHostingTlc`): `safety` holds over all 3,551,336 distinct states (45.1 million generated), depth 33, 28 seconds.

## Correspondence with the implementation

| Model | `GarnetPartitionBlobStorage` and Garnet |
| --- | --- |
| `takeOver` | `OpenAsync`: create the manifest if absent, check its epoch, break and acquire its lease, check the epoch again, write the manifest with this epoch, list every blob under the location. |
| `fence` | `AcquireAsync` on each listed blob: break its lease (period 0) and acquire it with the tenure's lease id. |
| `recover` | The confirming manifest update at the end of `OpenAsync`, then Garnet's recovery through `GarnetPartitionBlobDeviceFactory`, which lists and reads registered blobs only. |
| `flush` | A Tsavorite page write (`UploadPages` with the lease id) and the AOF commit Garnet waits for before replying (`--aof-commit-wait`). |
| `readStart`, `readEnd` | Journal read and catalog procedures take an exclusive lock, so Garnet logs them and their replies wait for an AOF commit. |
| `create`, `acquire`, `register` | `CreateAsync`: create the page blob with `If-None-Match: *` (or recreate it under the tenure's lease), acquire its lease, then register it in the manifest. |
| `reassign` | `GarnetJournalHostingCoordinator`: a compare-and-swap of the range map which assigns the partition to a new host at the next epoch. |

## Not modeled

- Deletion of segments (AOF truncation and checkpoint cleanup), which unregisters a blob before deleting it under the tenure's lease.
- Checkpoints, which are blobs written and registered like segments.
- Partial flushes, which Tsavorite's recovery treats as a crash would: it recovers up to the last complete commit.
