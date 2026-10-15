// Partition hosting and failover on shared blob storage, with fencing, for
// Orleans.Journaling on embedded Garnet partitions (phase 4, decision D12).
//
// A partition's durable state is a set of page blobs (AOF segments, here) in
// shared storage, and a manifest blob which registers the blobs that hold the
// partition's state. A hosting tenure is one host's hold on the partition at a
// hosting epoch. The coordinator assigns the partition to a new tenure whenever
// it decides that the current host is dead; the new tenure takes the storage
// over, recovers, and serves, while the previous tenure may keep running as a
// zombie until it observes the change. The range migration specification
// (specs/garnet-range-migration) treats a partition as a durable, linearizable
// store that is sometimes unavailable; this specification checks that failover
// preserves that abstraction: no tenure acknowledges a write, or answers a
// read, inconsistently with the state the latest tenure recovered.
//
// The protocol (GarnetPartitionBlobStorage):
// - Every blob write, manifest update, and deletion carries the tenure's lease
//   id, so the storage service rejects it once another tenure broke the lease.
// - A tenure creates a blob (if absent), acquires its lease, and only then
//   registers it in the manifest, before writing to it. Recovery reads only
//   registered blobs. Only a takeover breaks leases.
// - Takeover breaks and acquires the manifest's lease, checks and records the
//   epoch, breaks and acquires the lease of every existing blob, and only then
//   recovers.
// - Garnet acknowledges a write once its AOF flush completed. A read logs an
//   AOF record too (its procedure takes an exclusive lock), so it replies only
//   after a flush.
//
// Modeling choices:
// - A tenure's id is its hosting epoch. Blobs are AOF segments whose data is
//   the list of op ids flushed to them; recovery concatenates the registered
//   segments in order. Each storage request is one atomic step, as the service
//   executes it. Tenures know only their own state, not the manifest's.
// - Takeover's acquisition, epoch check, and listing of existing blobs form one
//   step: a zombie can create no blob in between which it could later write
//   to, since it can no longer register one.
// - A flush writes every pending op of the tenure, and any read's record, to
//   its current segment and acknowledges them, or fails the tenure, which then
//   neither acknowledges nor answers anything.
module partitionHosting {

  // ---------------------------------------------------------------- Types

  type Blob = { exists: bool, lease: int, data: List[int] }

  // A read in progress: its result (the tenure's state when it ran) and the
  // ops acknowledged before it began.
  type Read = { active: bool, result: Set[int], required: Set[int] }

  // phase: "idle" | "fencing" | "open" | "failed". targets: blobs a takeover
  // fences; fenced: those fenced so far. mem: the in-memory state; pending:
  // ops applied in memory but not yet flushed. seg: the segment the tenure
  // appends to (-1: none). creating/stage: a segment being created, after
  // "put" (created) and "acquired" (leased). marker: a read's AOF record awaits
  // a flush.
  type Tenure = {
    phase: str,
    targets: Set[int],
    fenced: Set[int],
    mem: List[int],
    pending: List[int],
    seg: int,
    creating: int,
    stage: str,
    marker: bool,
    read: Read,
  }

  type Manifest = { lease: int, epoch: int, listed: Set[int] }

  // ------------------------------------------------------------ Constants

  const TENURES: Set[int]
  const NUM_SEGMENTS: int
  const OPS: Set[int]

  pure val SEGMENTS = 0.to(NUM_SEGMENTS - 1)
  pure val SEGMENT_ORDER = range(0, NUM_SEGMENTS)
  pure val NO_BLOB: Blob = { exists: false, lease: -1, data: List() }
  pure val NO_READ: Read = { active: false, result: Set(), required: Set() }
  pure val IDLE: Tenure = {
    phase: "idle", targets: Set(), fenced: Set(), mem: List(), pending: List(),
    seg: -1, creating: -1, stage: "none", marker: false, read: NO_READ,
  }

  pure def toSet(l: List[int]): Set[int] = l.foldl(Set(), (acc, x) => acc.union(Set(x)))

  // ------------------------------------------------------------ State

  var manifest: Manifest
  var blobs: int -> Blob
  var ten: int -> Tenure
  // The latest tenure the coordinator assigned: the MetaStore's hosting epoch.
  var assigned: int
  var acked: Set[int]
  var issued: Set[int]
  // Set once a read returned a state which misses a write acknowledged before
  // the read began, or includes an unacknowledged write.
  var badRead: bool

  def blobOf(s: int): Blob = if (s >= 0) blobs.get(s) else NO_BLOB

  // The state a takeover recovers: the registered segments' data, in order.
  def recoverable: List[int] =
    SEGMENT_ORDER.foldl(List(), (acc, s) => if (manifest.listed.contains(s)) acc.concat(blobs.get(s).data) else acc)

  action init = all {
    manifest' = { lease: -1, epoch: -1, listed: Set() },
    blobs' = SEGMENTS.mapBy(_ => NO_BLOB),
    ten' = TENURES.mapBy(_ => IDLE),
    assigned' = 0,
    acked' = Set(),
    issued' = Set(),
    badRead' = false,
  }

  action setTenure(t: int, updated: Tenure): bool = all {
    ten' = ten.set(t, updated),
    manifest' = manifest, blobs' = blobs, assigned' = assigned, acked' = acked, issued' = issued, badRead' = badRead,
  }

  action failTenure(t: int): bool = setTenure(t, { ...ten.get(t), phase: "failed" })

  // ------------------------------------------------------------ Coordinator

  // The coordinator assigns the partition to the next tenure (epoch + 1), for
  // example because membership declared the current host dead, whether or not
  // it is.
  action reassign = all {
    TENURES.contains(assigned + 1),
    assigned' = assigned + 1,
    manifest' = manifest, blobs' = blobs, ten' = ten, acked' = acked, issued' = issued, badRead' = badRead,
  }

  // ------------------------------------------------------------ Takeover

  // Breaks and acquires the manifest's lease, checks that no later epoch holds
  // the storage, records this epoch, and lists the blobs to fence. With
  // `fenceManifest` false (a mutant), the earlier tenure keeps the manifest's lease.
  action takeOverWith(t: int, fenceManifest: bool): bool = {
    val tn = ten.get(t)
    all {
      tn.phase == "idle",
      t <= assigned,
      manifest.epoch <= t,
      manifest' = { lease: if (fenceManifest) t else manifest.lease, epoch: t, listed: manifest.listed },
      ten' = ten.set(t, { ...tn, phase: "fencing", targets: SEGMENTS.filter(s => blobs.get(s).exists) }),
      blobs' = blobs, assigned' = assigned, acked' = acked, issued' = issued, badRead' = badRead,
    }
  }

  action takeOver(t: int): bool = takeOverWith(t, true)

  // Breaks a blob's lease and acquires it.
  action fence(t: int, s: int): bool = {
    val tn = ten.get(t)
    val b = blobs.get(s)
    all {
      tn.phase == "fencing",
      tn.targets.contains(s),
      not(tn.fenced.contains(s)),
      blobs' = if (b.exists) blobs.set(s, { ...b, lease: t }) else blobs,
      ten' = ten.set(t, { ...tn, fenced: tn.fenced.union(Set(s)) }),
      manifest' = manifest, assigned' = assigned, acked' = acked, issued' = issued, badRead' = badRead,
    }
  }

  // Once every blob is fenced, confirms that the tenure still holds the
  // manifest (a lease-conditioned update), and recovers the registered
  // segments. A later takeover then follows every lease this tenure broke. With
  // `afterFencing` false (a mutant), it may recover first; with `confirm` false
  // (a mutant), it recovers without confirming.
  action recoverWith(t: int, afterFencing: bool, confirm: bool): bool = {
    val tn = ten.get(t)
    all {
      tn.phase == "fencing",
      not(afterFencing) or tn.targets.subseteq(tn.fenced),
      if (not(confirm) or manifest.lease == t) setTenure(t, {
        ...tn,
        phase: "open",
        mem: recoverable,
        pending: List(),
        seg: SEGMENT_ORDER.foldl(-1, (acc, s) => if (manifest.listed.contains(s)) s else acc),
      }) else failTenure(t),
    }
  }

  action recover(t: int): bool = recoverWith(t, true, true)

  // ------------------------------------------------------------ Serving

  // Applies a write in memory; it is acknowledged once flushed.
  action write(t: int, op: int): bool = {
    val tn = ten.get(t)
    all {
      tn.phase == "open",
      not(issued.contains(op)),
      ten' = ten.set(t, { ...tn, mem: tn.mem.append(op), pending: tn.pending.append(op) }),
      issued' = issued.union(Set(op)),
      manifest' = manifest, blobs' = blobs, assigned' = assigned, acked' = acked, badRead' = badRead,
    }
  }

  // Flushes the pending ops, and any read's record, to the current segment
  // under the tenure's lease, and acknowledges them. With `leased` false (a
  // mutant), the write ignores the lease.
  action flushWith(t: int, leased: bool): bool = {
    val tn = ten.get(t)
    val b = blobOf(tn.seg)
    all {
      tn.phase == "open",
      tn.seg >= 0,
      tn.pending.length() > 0 or tn.marker,
      if (b.exists and (not(leased) or b.lease == t)) all {
        blobs' = blobs.set(tn.seg, { ...b, data: b.data.concat(tn.pending) }),
        acked' = acked.union(toSet(tn.pending)),
        ten' = ten.set(t, { ...tn, pending: List(), marker: false }),
        manifest' = manifest, assigned' = assigned, issued' = issued, badRead' = badRead,
      } else failTenure(t),
    }
  }

  action flush(t: int): bool = flushWith(t, true)

  // A read observes the in-memory state and logs a record, whose flush it
  // waits for.
  action readStart(t: int): bool = {
    val tn = ten.get(t)
    all {
      tn.phase == "open",
      not(tn.read.active),
      setTenure(t, { ...tn, marker: true, read: { active: true, result: toSet(tn.mem), required: acked } }),
    }
  }

  // Replies once the read's record is flushed. With `logged` false (a
  // mutant), it replies without waiting.
  action readEndWith(t: int, logged: bool): bool = {
    val tn = ten.get(t)
    val r = tn.read
    all {
      tn.phase == "open",
      r.active,
      not(logged) or not(tn.marker),
      ten' = ten.set(t, { ...tn, read: NO_READ }),
      badRead' = (badRead or not(r.required.subseteq(r.result)) or not(r.result.subseteq(acked))),
      manifest' = manifest, blobs' = blobs, assigned' = assigned, acked' = acked, issued' = issued,
    }
  }

  action readEnd(t: int): bool = readEndWith(t, true)

  // ------------------------------------------------------------ Segments

  // Creates a new segment if absent (If-None-Match). An existing segment is
  // replaced only under this tenure's lease, from its takeover. With
  // `breakLease` true (a mutant), it breaks any lease to replace it.
  action createWith(t: int, s: int, breakLease: bool): bool = {
    val tn = ten.get(t)
    val b = blobs.get(s)
    all {
      tn.phase == "open",
      tn.stage == "none",
      s > tn.seg,
      if (not(b.exists)) all {
        blobs' = blobs.set(s, { exists: true, lease: -1, data: List() }),
        ten' = ten.set(t, { ...tn, creating: s, stage: "put" }),
        manifest' = manifest, assigned' = assigned, acked' = acked, issued' = issued, badRead' = badRead,
      } else if (b.lease == t or breakLease) all {
        blobs' = blobs.set(s, { exists: true, lease: t, data: List() }),
        ten' = ten.set(t, { ...tn, creating: s, stage: "acquired" }),
        manifest' = manifest, assigned' = assigned, acked' = acked, issued' = issued, badRead' = badRead,
      } else failTenure(t),
    }
  }

  action create(t: int, s: int): bool = createWith(t, s, false)

  // Acquires the new segment's lease, which fails if a takeover fenced it.
  action acquire(t: int): bool = {
    val tn = ten.get(t)
    val b = blobOf(tn.creating)
    all {
      tn.phase == "open",
      tn.stage == "put",
      if (b.lease == -1) all {
        blobs' = blobs.set(tn.creating, { ...b, lease: t }),
        ten' = ten.set(t, { ...tn, stage: "acquired" }),
        manifest' = manifest, assigned' = assigned, acked' = acked, issued' = issued, badRead' = badRead,
      } else failTenure(t),
    }
  }

  // Registers the new segment under the manifest's lease, then appends to it.
  // With `registered` false (a mutant), it appends without registering.
  action registerWith(t: int, registered: bool): bool = {
    val tn = ten.get(t)
    all {
      tn.phase == "open",
      tn.stage == "acquired",
      if (not(registered)) setTenure(t, { ...tn, seg: tn.creating, creating: -1, stage: "none" })
      else if (manifest.lease == t) all {
        manifest' = { ...manifest, listed: manifest.listed.union(Set(tn.creating)) },
        ten' = ten.set(t, { ...tn, seg: tn.creating, creating: -1, stage: "none" }),
        blobs' = blobs, assigned' = assigned, acked' = acked, issued' = issued, badRead' = badRead,
      } else failTenure(t),
    }
  }

  action register(t: int): bool = registerWith(t, true)

  // ------------------------------------------------------------ Steps

  action tenureStep(t: int): bool = {
    nondet s = SEGMENTS.oneOf()
    nondet op = OPS.oneOf()
    any { takeOver(t), fence(t, s), recover(t), write(t, op), flush(t), readStart(t), readEnd(t), create(t, s), acquire(t), register(t) }
  }

  action step = {
    nondet t = TENURES.oneOf()
    any { reassign, tenureStep(t) }
  }

  // ------------------------------------------------------------ Invariants

  // Every acknowledged write is in the registered segments, which a takeover recovers.
  val recoverableAcked = acked.subseteq(toSet(recoverable))

  // The serving tenure which holds the manifest has every acknowledged write.
  val holderHasAcked =
    (manifest.lease >= 0 and ten.get(manifest.lease).phase == "open") implies acked.subseteq(toSet(ten.get(manifest.lease).mem))

  // Every read returned every write acknowledged before it began, and only acknowledged writes.
  val readsLinearizable = not(badRead)

  val safety = recoverableAcked and holderHasAcked and readsLinearizable

  // ------------------------------------------------------------ Mutants
  //
  // Each mutant breaks one rule of the protocol. Simulation with
  // `--step=<mutant>` must find a safety violation.

  // Writes without the lease condition.
  action mutantUnfencedWrite = {
    nondet t = TENURES.oneOf()
    any { step, flushWith(t, false) }
  }

  // Replies to a read without logging it.
  action mutantUnfencedRead = {
    nondet t = TENURES.oneOf()
    any { step, readEndWith(t, false) }
  }

  // Takes over without the manifest's lease, so an earlier tenure still
  // registers blobs (and the takeover cannot confirm).
  action mutantUnfencedManifest = {
    nondet t = TENURES.oneOf()
    any { step, takeOverWith(t, false), recoverWith(t, true, false) }
  }

  // Recovers without confirming the manifest's lease after fencing, so a
  // slower, earlier takeover breaks the leases of a later one which has
  // already recovered.
  action mutantUnconfirmedRecovery = {
    nondet t = TENURES.oneOf()
    any { step, recoverWith(t, true, false) }
  }

  // Appends to a new segment without registering it.
  action mutantUnregisteredWrite = {
    nondet t = TENURES.oneOf()
    any { step, registerWith(t, false) }
  }

  // Breaks another tenure's lease to replace an existing segment.
  action mutantCreateBreaksLease = {
    nondet t = TENURES.oneOf()
    nondet s = SEGMENTS.oneOf()
    any { step, createWith(t, s, true) }
  }

  // Recovers before fencing every blob.
  action mutantRecoverBeforeFence = {
    nondet t = TENURES.oneOf()
    any { step, recoverWith(t, false, true) }
  }
}

// Configuration for random simulation and directed scenarios.
module partitionHostingSim {
  import partitionHosting(TENURES = Set(0, 1, 2), NUM_SEGMENTS = 3, OPS = Set(1, 2, 3)).*

  // Tenure 0 serves with segment 0 and acknowledges op 1.
  run serving =
    init
      .then(takeOver(0))
      .then(recover(0))
      .then(create(0, 0))
      .then(acquire(0))
      .then(register(0))
      .then(write(0, 1))
      .then(flush(0))

  // Tenure 1 takes over and recovers op 1.
  run tookOver =
    serving
      .then(reassign)
      .then(takeOver(1))
      .then(fence(1, 0))
      .then(recover(1))

  // The zombie's next write fails.
  run failoverTest =
    tookOver
      .then(write(0, 2))
      .then(flush(0))
      .expect(safety and ten.get(0).phase == "failed" and ten.get(1).mem == List(1) and acked == Set(1))

  // A write flushed during the takeover, before its segment is fenced, is recovered.
  run flushDuringTakeoverTest =
    serving
      .then(write(0, 2))
      .then(reassign)
      .then(takeOver(1))
      .then(flush(0))
      .then(fence(1, 0))
      .then(recover(1))
      .expect(safety and ten.get(1).mem == List(1, 2))

  // A zombie which writes nothing after the takeover still answers no read.
  run zombieReadTest =
    tookOver
      .then(write(1, 2))
      .then(flush(1))
      .then(readStart(0))
      .then(flush(0))
      .then(readEnd(0))
      .fail()

  // A zombie which created and acquired a segment before the takeover can neither register nor write it.
  run zombieSegmentTest =
    serving
      .then(create(0, 1))
      .then(acquire(0))
      .then(reassign)
      .then(takeOver(1))
      .then(fence(1, 0))
      .then(fence(1, 1))
      .then(recover(1))
      .then(register(0))
      .expect(safety and ten.get(0).phase == "failed" and not(manifest.listed.contains(1)))

  // The new tenure replaces the zombie's unregistered segment, which it fenced.
  run replaceFencedSegmentTest =
    zombieSegmentTest
      .then(create(1, 1))
      .then(register(1))
      .then(write(1, 2))
      .then(flush(1))
      .expect(safety and recoverable == List(1, 2))

  // An earlier takeover which fences blobs after a later one recovered fails
  // to confirm, and never serves.
  run concurrentTakeoverTest =
    serving
      .then(reassign)
      .then(reassign)
      .then(takeOver(1))
      .then(takeOver(2))
      .then(fence(2, 0))
      .then(recover(2))
      .then(fence(1, 0))
      .then(recover(1))
      .expect(safety and ten.get(1).phase == "failed" and ten.get(2).phase == "open")

  // A stale tenure cannot take over once a later one has.
  run staleTakeoverTest =
    serving
      .then(reassign)
      .then(reassign)
      .then(takeOver(2))
      .then(takeOver(1))
      .fail()

  // The new tenure serves writes on a segment of its own, and a read sees them.
  run newSegmentTest =
    tookOver
      .then(create(1, 1))
      .then(acquire(1))
      .then(register(1))
      .then(write(1, 2))
      .then(flush(1))
      .then(readStart(1))
      .then(flush(1))
      .then(readEnd(1))
      .expect(safety and recoverable == List(1, 2))

  // Mutant counterexamples.
  run unfencedWriteTest =
    tookOver
      .then(write(0, 2))
      .then(flushWith(0, false))
      .expect(not(safety))

  run unfencedReadTest =
    tookOver
      .then(write(1, 2))
      .then(flush(1))
      .then(readStart(0))
      .then(readEndWith(0, false))
      .expect(not(safety))

  run unfencedManifestTest =
    serving
      .then(reassign)
      .then(takeOverWith(1, false))
      .then(fence(1, 0))
      .then(recoverWith(1, true, false))
      .then(create(0, 1))
      .then(acquire(0))
      .then(register(0))
      .then(write(0, 2))
      .then(flush(0))
      .then(readStart(1))
      .then(flush(1))
      .then(readEnd(1))
      .expect(not(safety))

  run unregisteredWriteTest =
    serving
      .then(create(0, 1))
      .then(acquire(0))
      .then(registerWith(0, false))
      .then(write(0, 2))
      .then(flush(0))
      .expect(not(safety))

  run createBreaksLeaseTest =
    tookOver
      .then(create(1, 1))
      .then(acquire(1))
      .then(register(1))
      .then(write(1, 2))
      .then(flush(1))
      .then(createWith(0, 1, true))
      .expect(not(safety))

  run unconfirmedRecoveryTest =
    serving
      .then(reassign)
      .then(reassign)
      .then(takeOver(1))
      .then(takeOver(2))
      .then(fence(2, 0))
      .then(recover(2))
      .then(fence(1, 0))
      .then(recoverWith(1, true, false))
      .then(write(1, 2))
      .then(flush(1))
      .expect(not(safety))

  run recoverBeforeFenceTest =
    serving
      .then(reassign)
      .then(takeOver(1))
      .then(recoverWith(1, false, true))
      .then(write(0, 2))
      .then(flush(0))
      .expect(not(safety))
}

// Smaller configuration for exhaustive model checking with TLC.
module partitionHostingTlc {
  import partitionHosting(TENURES = Set(0, 1, 2), NUM_SEGMENTS = 2, OPS = Set(1, 2)).*
}
