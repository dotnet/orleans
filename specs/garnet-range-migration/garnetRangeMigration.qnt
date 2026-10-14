// Range handover, lazy migration, consolidation, and garbage collection for
// Orleans.Journaling on embedded Garnet partitions.
//
// Adapted from Roux's specs/range-migration/rangeMigration.qnt. Roux models a
// node-wide log in which a range's history is positioned before an in-band
// seal. Here a bucket's history is the set of per-journal records a partition
// holds, and a partition's bucket state (absent, open, sealed) is the seal:
// every journal operation runs in one transaction that checks the bucket is
// open, so an operation either commits before the seal or observes it.
//
// See src/Garnet/DESIGN.md.
//
// Modeling choices:
// - Partitions are the actors. A view maps each bucket to the partition that
//   owns it. Partition hosting is abstract: a partition is durable and
//   linearizable, and an unavailable partition simply takes no steps.
// - A journal record is a list of appended values (a write's value is its op
//   id), a version (the content ETag), and the ids of the mutations it has
//   applied (the recent-ETag list, unbounded here). Tombstones and absent
//   markers are records that read as absent.
// - Every write is conditional: on the version the client observed, or on
//   the journal's absence (expect = -1).
// - Any reply can be lost; the client later retries the same op.
// - Pulls (on NeedPull) and consolidation install a journal copied from the
//   bucket's migration chain if the destination holds no record for it.
// - A view change moves one or more buckets to one partition, which covers
//   moves, splits, and merges. Requests carry no view version (D17), so
//   clients may send any operation to any partition. In `step`, views change
//   freely, which allows chained migrations; safety holds for every sequence
//   of views. Liveness additionally needs the coordinator's rule
//   (`mayMove`, `coordinatedStep`).
module garnetRangeMigration {

  // ---------------------------------------------------------------- Types

  type JRec = { kind: str, vals: List[int], ver: int, ids: Set[int] }

  // st: "absent" | "open" | "sealed". ver: view at which the partition opened
  // the bucket. sealVer: view whose change sealed it. src: partition holding
  // the bucket's earlier history (-1: none).
  type Bucket = { st: str, ver: int, sealVer: int, src: int, done: bool, reclaimed: bool }

  type Op = { key: str, del: bool, expect: int }

  type Walk = { q: int, finished: bool, err: bool, rec: JRec, visited: Set[int] }

  // ------------------------------------------------------------ Constants

  const PARTITIONS: Set[int]
  const KEYS: Set[str]
  const KEY_BUCKET: str -> int
  const MAX_VIEWS: int
  const MAX_OPS: int

  pure val BUCKETS = Set(0, 1)
  pure val DEPTH = MAX_VIEWS + 2
  pure val OPS = 0.to(MAX_OPS - 1)

  pure val NONE: JRec = { kind: "none", vals: List(), ver: 0, ids: Set() }
  pure val ABSENT_MARKER: JRec = { kind: "absent", vals: List(), ver: 0, ids: Set() }
  pure val NO_BUCKET: Bucket = { st: "absent", ver: -1, sealVer: -1, src: -1, done: false, reclaimed: false }

  pure def keysOf(b: int): Set[str] = KEYS.filter(k => KEY_BUCKET.get(k) == b)
  pure def isLive(r: JRec): bool = r.kind == "live"
  pure def isPresent(r: JRec): bool = r.kind != "none"

  // ------------------------------------------------------------ State

  var views: List[int -> int]
  var bkt: int -> (int -> Bucket)
  var jr: int -> (str -> JRec)
  // Oracle: each journal's content after every committed write.
  var truth: str -> List[int]
  var ops: int -> Op
  // "none" | "pending" (reply lost) | "acked" | "conflict"
  var outcome: int -> str
  var applied: int -> int
  var nextOp: int

  // ------------------------------------------------------------ Derived state

  // The partition that most recently opened the bucket.
  def currentOpener(b: int): int =
    PARTITIONS.fold(-1, (acc, p) =>
      if (bkt.get(p).get(b).st == "absent") acc
      else if (acc < 0) p
      else if (bkt.get(p).get(b).ver > bkt.get(acc).get(b).ver) p
      else acc)

  def openAt(b: int): Set[int] = PARTITIONS.filter(p => bkt.get(p).get(b).st == "open")

  // Reads key k through the migration chain starting at partition q0, as a
  // pull does. Reading a reclaimed bucket is an error, never absence.
  def walk(q0: int, k: str): Walk = {
    val b = KEY_BUCKET.get(k)
    range(0, DEPTH).foldl(
      { q: q0, finished: false, err: false, rec: NONE, visited: Set() },
      (s, _) =>
        if (s.finished) s
        else if (s.q < 0) { ...s, finished: true }
        else {
          val bs = bkt.get(s.q).get(b)
          val r = jr.get(s.q).get(k)
          val v = s.visited.union(Set(s.q))
          if (bs.reclaimed) { ...s, finished: true, err: true, visited: v }
          else if (isPresent(r)) { ...s, finished: true, rec: r, visited: v }
          else if (bs.done or bs.src < 0) { ...s, finished: true, visited: v }
          else { ...s, q: bs.src, visited: v }
        })
  }

  def readAt(p: int, k: str): { err: bool, vals: List[int] } = {
    val w = walk(p, k)
    { err: w.err or not(w.finished), vals: w.rec.vals }
  }

  // Applies op `id` to record r. Returns the new record and the outcome.
  pure def apply(r: JRec, id: int, op: Op, checkIds: bool): { rec: JRec, status: str, commit: bool } =
    if (checkIds and r.ids.contains(id)) { rec: r, status: "acked", commit: false }
    else {
      val ok = if (op.expect < 0) not(isLive(r)) else isLive(r) and r.ver == op.expect
      if (not(ok) or (op.del and not(isLive(r)))) { rec: r, status: "conflict", commit: false }
      else if (op.del) { rec: { kind: "tomb", vals: List(), ver: r.ver + 1, ids: r.ids.union(Set(id)) }, status: "acked", commit: true }
      else {
        val base = if (isLive(r)) r.vals else List()
        { rec: { kind: "live", vals: base.append(id), ver: r.ver + 1, ids: r.ids.union(Set(id)) }, status: "acked", commit: true }
      }
    }

  // ------------------------------------------------------------ Actions

  action init = all {
    views' = List(Map(0 -> 1, 1 -> 2)),
    bkt' = PARTITIONS.mapBy(p => BUCKETS.mapBy(b =>
      if ((p == 1 and b == 0) or (p == 2 and b == 1))
        { st: "open", ver: 0, sealVer: -1, src: -1, done: true, reclaimed: false }
      else NO_BUCKET)),
    jr' = PARTITIONS.mapBy(_ => KEYS.mapBy(_ => NONE)),
    truth' = KEYS.mapBy(_ => List()),
    ops' = OPS.mapBy(_ => { key: "", del: false, expect: -1 }),
    outcome' = OPS.mapBy(_ => "none"),
    applied' = OPS.mapBy(_ => 0),
    nextOp' = 0,
  }

  // Runs op `id` (already recorded in `opsNext`) at partition p, which must
  // hold the bucket open (the in-band seal check) and hold the journal or have
  // completed migration (otherwise the partition answers NeedPull and the
  // client pulls first). A lost reply leaves the outcome pending.
  action executeWith(p: int, id: int, op: Op, checkIds: bool, sealCheck: bool, lost: bool): bool = {
    val b = KEY_BUCKET.get(op.key)
    val bs = bkt.get(p).get(b)
    val r = jr.get(p).get(op.key)
    val res = apply(r, id, op, checkIds)
    all {
      if (sealCheck) bs.st == "open" else bs.st != "absent",
      isPresent(r) or bs.done,
      jr' = jr.set(p, jr.get(p).set(op.key, res.rec)),
      truth' = if (res.commit) truth.set(op.key, res.rec.vals) else truth,
      applied' = if (res.commit) applied.set(id, applied.get(id) + 1) else applied,
      outcome' = outcome.set(id, if (lost) "pending" else res.status),
    }
  }

  // A client issues a new write, conditional on what it observed: the
  // current version, or a stale one (a duplicate activation).
  action writeWith(p: int, k: str, del: bool, stale: bool, lost: bool, sealCheck: bool): bool = {
    val r = jr.get(p).get(k)
    val expected = if (stale) r.ver - 1 else if (isLive(r)) r.ver else -1
    val id = nextOp
    val op = { key: k, del: del, expect: expected }
    all {
      nextOp < MAX_OPS,
      ops' = ops.set(id, op),
      nextOp' = nextOp + 1,
      executeWith(p, id, op, true, sealCheck, lost),
      views' = views, bkt' = bkt,
    }
  }

  action write(p: int, k: str, del: bool, stale: bool, lost: bool): bool = writeWith(p, k, del, stale, lost, true)

  // A client retries an op whose reply was lost, at whichever partition it
  // routes to.
  action retryWith(p: int, id: int, checkIds: bool, lost: bool): bool = all {
    outcome.get(id) == "pending",
    executeWith(p, id, ops.get(id), checkIds, true, lost),
    views' = views, bkt' = bkt, ops' = ops, nextOp' = nextOp,
  }

  action retry(p: int, id: int, lost: bool): bool = retryWith(p, id, true, lost)

  // A view change moves the buckets `bs` to partition p in one version: a
  // move of one bucket, a split (some of a partition's buckets), or a merge
  // (buckets of several partitions into one). Each bucket is then handed over
  // on its own. Requests carry no view version (D17): a partition accepts an
  // operation if it holds the bucket open, whatever view the client holds, so
  // clients may route to any partition.
  action changeViewWith(bs: Set[int], p: int, coordinated: bool): bool = {
    val last = views[views.length() - 1]
    all {
      views.length() < MAX_VIEWS,
      bs.size() > 0,
      bs.forall(b => last.get(b) != p),
      not(coordinated) or bs.forall(b => mayMove(b, p)),
      views' = views.append(bs.fold(last, (m, b) => m.set(b, p))),
      bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp,
    }
  }

  action changeViews(bs: Set[int], p: int): bool = changeViewWith(bs, p, false)

  action changeView(b: int, p: int): bool = changeViews(Set(b), p)

  // Partition p seals bucket b once a later view moves b elsewhere.
  action seal(p: int, b: int): bool = {
    val bs = bkt.get(p).get(b)
    val laterView = range(0, MAX_VIEWS).foldl(-1, (acc, w) =>
      if (acc >= 0 or w <= bs.ver or w >= views.length()) acc
      else if (views[w].get(b) != p) w else acc)
    all {
      bs.st == "open",
      laterView >= 0,
      bkt' = bkt.set(p, bkt.get(p).set(b, { ...bs, st: "sealed", sealVer: laterView })),
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp,
    }
  }

  // Partition m opens bucket b at view v. The current opener must have sealed
  // the bucket. A partition reopens a bucket it still holds only if it is the
  // current opener, keeping its history; otherwise it must not hold the bucket
  // at all, or must have reclaimed it (D11).
  action openWith(m: int, b: int, v: int, requireSeal: bool): bool = {
    val cur = currentOpener(b)
    val curState = bkt.get(cur).get(b)
    val mine = bkt.get(m).get(b)
    val opened =
      if (cur == m) { ...mine, st: "open", ver: v, sealVer: -1 }
      else { st: "open", ver: v, sealVer: -1, src: cur, done: false, reclaimed: false }
    all {
      cur >= 0,
      v < views.length(),
      views[v].get(b) == m,
      v > curState.ver,
      not(requireSeal) or curState.st == "sealed",
      cur == m or mine.st == "absent" or mine.reclaimed,
      bkt' = bkt.set(m, bkt.get(m).set(b, opened)),
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp,
    }
  }

  action open(m: int, b: int, v: int): bool = openWith(m, b, v, true)

  // Pull or consolidation: installs k in p from p's migration chain, only if p
  // holds no record for k. A journal the chain does not hold installs as an
  // absent marker. Mutation ids travel with the record.
  action installWith(p: int, k: str, conditional: bool): bool = {
    val b = KEY_BUCKET.get(k)
    val bs = bkt.get(p).get(b)
    val w = walk(bs.src, k)
    val rec = if (w.rec.kind == "live" or w.rec.kind == "tomb") w.rec else ABSENT_MARKER
    all {
      bs.st == "open",
      not(bs.done),
      bs.src >= 0,
      not(conditional) or not(isPresent(jr.get(p).get(k))),
      w.finished,
      not(w.err),
      jr' = jr.set(p, jr.get(p).set(k, rec)),
      views' = views, bkt' = bkt, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp,
    }
  }

  action install(p: int, k: str): bool = installWith(p, k, true)

  action markDoneWith(p: int, b: int, early: bool): bool = {
    val bs = bkt.get(p).get(b)
    all {
      bs.st == "open",
      not(bs.done),
      early or keysOf(b).forall(k => isPresent(jr.get(p).get(k))),
      bkt' = bkt.set(p, bkt.get(p).set(b, { ...bs, done: true })),
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp,
    }
  }

  action markDone(p: int, b: int): bool = markDoneWith(p, b, false)

  // Whether a read path can reach partition a's copy of bucket b: the walk
  // from the bucket's current opener visits it.
  def reachable(a: int, b: int): bool =
    currentOpener(b) == a
      or keysOf(b).exists(k => walk(currentOpener(b), k).visited.contains(a))
      or PARTITIONS.exists(p => bkt.get(p).get(b).st == "open"
        and keysOf(b).exists(k => walk(p, k).visited.contains(a)))

  // A sealed partition reclaims a bucket no read path reaches. Its bucket state
  // stays sealed and marked reclaimed, so a read there fails instead of reading
  // as absent.
  action reclaimWith(a: int, b: int, early: bool): bool = {
    val bs = bkt.get(a).get(b)
    all {
      bs.st == "sealed",
      not(bs.reclaimed),
      early or not(reachable(a, b)),
      bkt' = bkt.set(a, bkt.get(a).set(b, { ...bs, reclaimed: true })),
      jr' = jr.set(a, jr.get(a).keys().mapBy(k =>
        if (KEY_BUCKET.get(k) == b) NONE else jr.get(a).get(k))),
      views' = views, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp,
    }
  }

  action reclaim(a: int, b: int): bool = reclaimWith(a, b, false)

  // Drops a tombstone or absent marker once migration is done (D5). A
  // tombstone also carries the mutation ids retries rely on, so it is dropped
  // only when no op on the journal awaits a retry (the implementation's
  // retention bound); an absent marker carries none.
  action dropMarker(p: int, k: str): bool = {
    val b = KEY_BUCKET.get(k)
    val bs = bkt.get(p).get(b)
    val r = jr.get(p).get(k)
    all {
      bs.st == "open",
      bs.done,
      r.kind == "absent"
        or (r.kind == "tomb" and OPS.forall(id => not(outcome.get(id) == "pending" and ops.get(id).key == k))),
      jr' = jr.set(p, jr.get(p).set(k, NONE)),
      views' = views, bkt' = bkt, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp,
    }
  }

  // The coordinator's rule for committing a view that moves bucket b to p
  // (D11). The previous move of b must have opened (its destination is the
  // current opener), so no partition opens at a superseded view. And no read
  // path may reach p's copy of b, if it holds one: p never held b, or a later
  // opener on its chain is done. Otherwise p cannot reopen, since its copy is
  // reclaimable only once the chain's newest opener is done, and that opener
  // is sealed by this move. Moves out of an incomplete migration
  // (failure-driven moves) therefore chain only through partitions off the
  // chain. Safety does not depend on this rule; `step` changes views freely.
  def mayMove(b: int, p: int): bool = {
    val last = views[views.length() - 1]
    val cur = currentOpener(b)
    all {
      last.get(b) == cur,
      bkt.get(cur).get(b).st == "open",
      not(reachable(p, b)),
    }
  }

  // Client actions: new writes and retries of writes whose reply was lost.
  action clientStep = {
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    nondet id = OPS.oneOf()
    nondet del = Bool.oneOf()
    nondet stale = Bool.oneOf()
    nondet lost = Bool.oneOf()
    any {
      write(p, k, del, stale, lost),
      retry(p, id, lost),
    }
  }

  // Partition actions which carry out migrations, each the subject of a
  // fairness condition.
  action sealAny = { nondet p = PARTITIONS.oneOf() nondet b = BUCKETS.oneOf() seal(p, b) }
  action openAny = {
    nondet p = PARTITIONS.oneOf()
    nondet b = BUCKETS.oneOf()
    nondet v = 0.to(MAX_VIEWS - 1).oneOf()
    open(p, b, v)
  }
  action installAny = { nondet p = PARTITIONS.oneOf() nondet k = KEYS.oneOf() install(p, k) }
  action markDoneAny = { nondet p = PARTITIONS.oneOf() nondet b = BUCKETS.oneOf() markDone(p, b) }
  action reclaimAny = { nondet p = PARTITIONS.oneOf() nondet b = BUCKETS.oneOf() reclaim(p, b) }
  action dropMarkerAny = { nondet p = PARTITIONS.oneOf() nondet k = KEYS.oneOf() dropMarker(p, k) }

  action protocolStep = any { sealAny, openAny, installAny, markDoneAny, reclaimAny, dropMarkerAny }

  action step = {
    nondet bs = BUCKETS.powerset().exclude(Set(Set())).oneOf()
    nondet p = PARTITIONS.oneOf()
    any { clientStep, changeViews(bs, p), protocolStep }
  }

  // As `step`, with views changed only by the coordinator's rule.
  action coordinatedStep = {
    nondet bs = BUCKETS.powerset().exclude(Set(Set())).oneOf()
    nondet p = PARTITIONS.oneOf()
    any { clientStep, changeViewWith(bs, p, true), protocolStep }
  }

  // ------------------------------------------------------------ Invariants

  // At most one partition accepts writes for a bucket.
  val singleOpen = BUCKETS.forall(b => openAt(b).size() <= 1)

  // Reads through the bucket's current opener return the content after every
  // committed write (so every acknowledged write is visible); deleted and
  // never-written journals read as absent; and no read path reaches
  // reclaimed data, which reads as an error.
  val durability = BUCKETS.forall(b => {
    val cur = currentOpener(b)
    keysOf(b).forall(k => {
      val r = readAt(cur, k)
      not(r.err) and r.vals == truth.get(k)
    })
  })

  // Each op applies at most once; an acknowledged op committed; an op reported
  // as a conflict did not commit.
  val exactlyOnce = OPS.forall(id => and {
    applied.get(id) <= 1,
    outcome.get(id) == "acked" implies applied.get(id) == 1,
    outcome.get(id) == "conflict" implies applied.get(id) == 0,
  })

  val safety = singleOpen and durability and exactlyOnce

  // ------------------------------------------------------------ Liveness

  // Bucket b's latest move is complete: its owner in the latest view holds it
  // open with migration done, and every other partition has reclaimed its copy.
  def complete(b: int): bool = {
    val o = views[views.length() - 1].get(b)
    val os = bkt.get(o).get(b)
    and {
      os.st == "open",
      os.done,
      PARTITIONS.forall(q => q == o or bkt.get(q).get(b).st == "absent" or bkt.get(q).get(b).reclaimed),
    }
  }

  val allComplete = BUCKETS.forall(b => complete(b))

  val vars = (views, bkt, jr, truth, ops, outcome, applied, nextOp)

  temporal fairness = and {
    weakFair(sealAny, vars),
    weakFair(openAny, vars),
    weakFair(installAny, vars),
    weakFair(markDoneAny, vars),
    weakFair(reclaimAny, vars),
  }

  // Under weak fairness of sealing, opening, installing, completion, and
  // reclamation, every migration completes and its sources are reclaimed.
  // Views change finitely often (MAX_VIEWS), so this holds from the last view
  // change on. It requires the coordinator's rule: check it with
  // `--step=coordinatedStep`.
  temporal migrationsComplete = fairness implies always(eventually(allComplete))

  // ------------------------------------------------------------ Mutants
  //
  // Each mutant breaks one rule of the design. Simulation with
  // `--step=<mutant>` must find a safety violation.

  // Opens without waiting for the current opener's seal.
  action mutantOpenBeforeSeal = {
    nondet p = PARTITIONS.oneOf()
    nondet b = BUCKETS.oneOf()
    nondet v = 0.to(MAX_VIEWS - 1).oneOf()
    any { step, openWith(p, b, v, false) }
  }

  // Installs a pulled journal even if the destination already holds a newer one.
  action mutantUnconditionalInstall = {
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    any { step, installWith(p, k, false) }
  }

  // Deletes by removing the record instead of writing a tombstone, so before
  // migration is done a later read falls back to the source.
  action deleteWithoutTombstone(p: int, k: str): bool = {
    val b = KEY_BUCKET.get(k)
    val bs = bkt.get(p).get(b)
    val r = jr.get(p).get(k)
    val id = nextOp
    all {
      nextOp < MAX_OPS,
      bs.st == "open",
      isLive(r),
      ops' = ops.set(id, { key: k, del: true, expect: r.ver }),
      nextOp' = nextOp + 1,
      jr' = jr.set(p, jr.get(p).set(k, NONE)),
      truth' = truth.set(k, List()),
      applied' = applied.set(id, 1),
      outcome' = outcome.set(id, "acked"),
      views' = views, bkt' = bkt,
    }
  }

  action mutantDeleteWithoutTombstone = {
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    any { step, deleteWithoutTombstone(p, k) }
  }

  // Reclaims a sealed bucket that a read path still reaches.
  action mutantReclaimBeforeDone = {
    nondet p = PARTITIONS.oneOf()
    nondet b = BUCKETS.oneOf()
    any { step, reclaimWith(p, b, true) }
  }

  // Marks migration done before every journal is consolidated.
  action mutantMarkDoneEarly = {
    nondet p = PARTITIONS.oneOf()
    nondet b = BUCKETS.oneOf()
    any { step, markDoneWith(p, b, true) }
  }

  // Retries without recognizing the op's own committed mutation.
  action mutantRetryWithoutIdempotence = {
    nondet p = PARTITIONS.oneOf()
    nondet id = OPS.oneOf()
    nondet lost = Bool.oneOf()
    any { step, retryWith(p, id, false, lost) }
  }

  // Acknowledges a stale writer's write in a sealed bucket (the Delos erratum).
  action mutantWriteAfterSeal = {
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    nondet del = Bool.oneOf()
    any { step, writeWith(p, k, del, false, false, false) }
  }
}

// Configuration for random simulation and directed scenarios.
module garnetRangeMigrationSim {
  import garnetRangeMigration(
    PARTITIONS = Set(1, 2, 3),
    KEYS = Set("a", "b", "c"),
    KEY_BUCKET = Map("a" -> 0, "b" -> 0, "c" -> 1),
    MAX_VIEWS = 5,
    MAX_OPS = 5
  ).*

  // Moves bucket 0 from partition 1 to 2.
  run moveBucket0 =
    init
      .then(write(1, "a", false, false, false))
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(open(2, 0, 1))

  // Pulls, writes, consolidates, completes, and reclaims the source.
  run handoverTest =
    moveBucket0
      .then(install(2, "a"))
      .then(write(2, "a", false, false, false))
      .then(install(2, "b"))
      .then(markDone(2, 0))
      .then(reclaim(1, 0))
      .expect(safety and truth.get("a") == List(0, 1))

  // A delete during migration leaves a tombstone, so the journal does not
  // resurrect from the source.
  run deleteDuringMigrationTest =
    moveBucket0
      .then(install(2, "a"))
      .then(write(2, "a", true, false, false))
      .expect(safety and truth.get("a") == List())

  // A write whose reply was lost is retried at the new owner after migration
  // and reports success without applying twice.
  run retryAcrossMigrationTest =
    init
      .then(write(1, "a", false, false, true))
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(open(2, 0, 1))
      .then(install(2, "a"))
      .then(retry(2, 0, false))
      .expect(safety and outcome.get(0) == "acked")

  // A stale writer's write in the sealed source is rejected.
  run staleWriterRejectedTest =
    moveBucket0
      .then(write(1, "a", false, false, false))
      .fail()

  // The bucket returns to its earlier owner only after that owner reclaims it.
  run returnAfterReclaimTest =
    moveBucket0
      .then(install(2, "a"))
      .then(install(2, "b"))
      .then(markDone(2, 0))
      .then(reclaim(1, 0))
      .then(changeView(0, 1))
      .then(seal(2, 0))
      .then(open(1, 0, 2))
      .then(install(1, "a"))
      .expect(safety and truth.get("a") == List(0))

  // Chained migration: bucket 0 moves 1 -> 2 -> 3 before 2 is done. A pull at 3
  // walks the chain newest first: "a" comes from 2, which holds the newer
  // write, and "b", held by neither, installs as an absent marker. Once 3 is
  // done, no read path reaches 1 or 2, and both reclaim.
  run chainedMigrationTest =
    moveBucket0
      .then(install(2, "a"))
      .then(write(2, "a", false, false, false))
      .then(changeView(0, 3))
      .then(seal(2, 0))
      .then(open(3, 0, 2))
      .then(install(3, "a"))
      .then(install(3, "b"))
      .then(markDone(3, 0))
      .then(reclaim(2, 0))
      .then(reclaim(1, 0))
      .expect(safety and allComplete and truth.get("a") == List(0, 1)
        and jr.get(3).get("a").vals == List(0, 1) and jr.get(3).get("b").kind == "absent")

  // A pull follows the chain past a link that never held the journal.
  run chainedPullTest =
    moveBucket0
      .then(changeView(0, 3))
      .then(seal(2, 0))
      .then(open(3, 0, 2))
      .then(install(3, "a"))
      .expect(safety and jr.get(3).get("a").vals == List(0))

  // While a later opener's migration is incomplete, the chain still reaches
  // the earlier copies, so they are not reclaimed.
  run chainPinsSourcesTest =
    moveBucket0
      .then(changeView(0, 3))
      .then(seal(2, 0))
      .then(open(3, 0, 2))
      .then(reclaim(1, 0))
      .fail()

  // Merge: one view moves bucket 0 (from 1) and bucket 1 (from 2) to 3.
  run mergeTest =
    init
      .then(write(1, "a", false, false, false))
      .then(write(2, "c", false, false, false))
      .then(changeViews(Set(0, 1), 3))
      .then(seal(1, 0))
      .then(seal(2, 1))
      .then(open(3, 0, 1))
      .then(open(3, 1, 1))
      .then(install(3, "a"))
      .then(install(3, "b"))
      .then(install(3, "c"))
      .then(markDone(3, 0))
      .then(markDone(3, 1))
      .then(reclaim(1, 0))
      .then(reclaim(2, 1))
      .expect(safety and allComplete and views[1] == Map(0 -> 3, 1 -> 3))

  // Split: after the merge, bucket 1 leaves 3 for 1, which never held it.
  run splitTest =
    mergeTest
      .then(changeView(1, 1))
      .then(seal(3, 1))
      .then(open(1, 1, 2))
      .then(install(1, "c"))
      .then(markDone(1, 1))
      .then(reclaim(3, 1))
      .expect(safety and allComplete and truth.get("c") == List(1))

  // Without the coordinator's rule, a bucket that returns to a partition whose
  // sealed copy is still on the chain cannot open there: the copy is not
  // reclaimable until the chain's newest opener is done, and that opener is
  // sealed. Safety holds, but the migration stalls.
  run returnToChainedCopyStallsTest =
    moveBucket0
      .then(changeView(0, 1))
      .then(seal(2, 0))
      .then(open(1, 0, 2))
      .fail()

  // The coordinator's rule refuses that move until 2 is done, and allows it to
  // a partition which holds no copy.
  run coordinatorRuleTest =
    moveBucket0
      .then(changeViewWith(Set(0), 1, true))
      .fail()

  run coordinatorRuleAllowsChainToFreshPartitionTest =
    moveBucket0
      .then(changeViewWith(Set(0), 3, true))
      .expect(views.length() == 3)

  // A move commits only after the previous move's destination opened, so no
  // partition opens at a superseded view.
  run coordinatorRuleWaitsForOpenTest =
    init
      .then(changeViewWith(Set(0), 2, true))
      .then(changeViewWith(Set(0), 3, true))
      .fail()

  run coordinatorRuleAllowsReturnAfterDoneTest =
    moveBucket0
      .then(install(2, "a"))
      .then(install(2, "b"))
      .then(markDone(2, 0))
      .then(changeViewWith(Set(0), 1, true))
      .then(seal(2, 0))
      .then(reclaim(1, 0))
      .then(open(1, 0, 2))
      .expect(safety and openAt(0) == Set(1))
  run deleteWithoutTombstoneResurrectsTest =
    moveBucket0
      .then(install(2, "a"))
      .then(deleteWithoutTombstone(2, "a"))
      .expect(not(safety))

  run unconditionalInstallLosesWriteTest =
    moveBucket0
      .then(install(2, "a"))
      .then(write(2, "a", false, false, false))
      .then(installWith(2, "a", false))
      .expect(not(safety))

  run reclaimBeforeDoneTest =
    moveBucket0
      .then(reclaimWith(1, 0, true))
      .expect(not(safety))

  run retryWithoutIdempotenceTest =
    init
      .then(write(1, "a", false, false, true))
      .then(retryWith(1, 0, false, false))
      .expect(not(safety))

  // Once the destination has pulled, a write acknowledged in the sealed source
  // is lost.
  run writeAfterSealTest =
    moveBucket0
      .then(install(2, "a"))
      .then(writeWith(1, "a", false, false, false, false))
      .expect(not(safety))
}

// Smaller configuration for exhaustive model checking with TLC.
module garnetRangeMigrationTlc {
  import garnetRangeMigration(
    PARTITIONS = Set(1, 2, 3),
    KEYS = Set("a", "c"),
    KEY_BUCKET = Map("a" -> 0, "c" -> 1),
    MAX_VIEWS = 3,
    MAX_OPS = 2
  ).*
}

// Configuration for checking liveness with TLC.
module garnetRangeMigrationLive {
  import garnetRangeMigration(
    PARTITIONS = Set(1, 2, 3),
    KEYS = Set("a", "c"),
    KEY_BUCKET = Map("a" -> 0, "c" -> 1),
    MAX_VIEWS = 3,
    MAX_OPS = 1
  ).*
}
