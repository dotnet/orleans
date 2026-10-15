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
//   applied (the recent-ETag list). Ids are evicted only once no retry of
//   their op can arrive (D28). Tombstones and absent markers are records that
//   read as absent.
// - Every write is conditional: on the version the client observed, or on
//   the journal's absence (expect = -1).
// - Any reply can be lost; the client later retries the same op.
// - Pulls (on NeedPull) and consolidation are two requests: a fetch from the
//   bucket's migration chain, and a later install, fenced by the opening the
//   destination fetched for, if the destination holds no record (D27).
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

  type Walk = { q: int, finished: bool, err: bool, rec: JRec }

  // A pull in flight: a journal fetched from p's migration chain, to be
  // delivered to p. fence: the view at which p had opened the bucket when it
  // fetched.
  type Pull = { active: bool, p: int, k: str, fence: int, rec: JRec }

  // ------------------------------------------------------------ Constants

  const PARTITIONS: Set[int]
  const KEYS: Set[str]
  const KEY_BUCKET: str -> int
  const MAX_VIEWS: int
  const MAX_OPS: int
  const MAX_PULLS: int

  pure val BUCKETS = Set(0, 1)
  pure val DEPTH = MAX_VIEWS + 2
  pure val OPS = 0.to(MAX_OPS - 1)
  pure val PULL_SLOTS = 0.to(MAX_PULLS - 1)

  pure val NONE: JRec = { kind: "none", vals: List(), ver: 0, ids: Set() }
  pure val ABSENT_MARKER: JRec = { kind: "absent", vals: List(), ver: 0, ids: Set() }
  pure val NO_BUCKET: Bucket = { st: "absent", ver: -1, sealVer: -1, src: -1, done: false, reclaimed: false }
  pure val NO_PULL: Pull = { active: false, p: -1, k: "", fence: -1, rec: NONE }

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
  var pulls: int -> Pull

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
      { q: q0, finished: false, err: false, rec: NONE },
      (s, _) =>
        if (s.finished) s
        else if (s.q < 0) { ...s, finished: true }
        else {
          val bs = bkt.get(s.q).get(b)
          val r = jr.get(s.q).get(k)
          if (bs.reclaimed) { ...s, finished: true, err: true }
          else if (isPresent(r)) { ...s, finished: true, rec: r }
          else if (bs.done or bs.src < 0) { ...s, finished: true }
          else { ...s, q: bs.src }
        })
  }

  // The partitions on bucket b's migration chain from q0: q0, then each
  // source in turn, stopping after a bucket that is done, has no source, or is
  // reclaimed. It contains every partition a read of any journal of b from q0
  // can visit, and depends only on bucket state, as the coordinator observes
  // it through migration phases.
  def chain(q0: int, b: int): Set[int] =
    range(0, DEPTH).foldl({ q: q0, out: Set() }, (s, _) =>
      if (s.q < 0) s
      else {
        val bs = bkt.get(s.q).get(b)
        val out = s.out.union(Set(s.q))
        if (bs.reclaimed or bs.done or bs.src < 0) { q: -1, out: out }
        else { q: bs.src, out: out }
      }).out

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
    pulls' = PULL_SLOTS.mapBy(_ => NO_PULL),
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
      views' = views, bkt' = bkt, pulls' = pulls,
    }
  }

  action write(p: int, k: str, del: bool, stale: bool, lost: bool): bool = writeWith(p, k, del, stale, lost, true)

  // A client retries an op whose reply was lost, at whichever partition it
  // routes to.
  action retryWith(p: int, id: int, checkIds: bool, lost: bool): bool = all {
    outcome.get(id) == "pending",
    executeWith(p, id, ops.get(id), checkIds, true, lost),
    views' = views, bkt' = bkt, ops' = ops, nextOp' = nextOp, pulls' = pulls,
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
      bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp, pulls' = pulls,
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
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp, pulls' = pulls,
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
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp, pulls' = pulls,
    }
  }

  action open(m: int, b: int, v: int): bool = openWith(m, b, v, true)

  // Pull or consolidation, first half: a client (or B's consolidator) reads
  // journal k, which p lacks, from p's migration chain and records the read in
  // pull slot i. A journal the chain does not hold reads as an absent marker.
  // Sealed sources are immutable until reclaimed, so the read is stable, and
  // reading a reclaimed source fails. Mutation ids travel with the record.
  action fetch(i: int, p: int, k: str): bool = {
    val b = KEY_BUCKET.get(k)
    val bs = bkt.get(p).get(b)
    val w = walk(bs.src, k)
    val rec = if (w.rec.kind == "live" or w.rec.kind == "tomb") w.rec else ABSENT_MARKER
    all {
      not(pulls.get(i).active),
      bs.st == "open",
      not(bs.done),
      bs.src >= 0,
      not(isPresent(jr.get(p).get(k))),
      w.finished,
      not(w.err),
      pulls' = pulls.set(i, { active: true, p: p, k: k, fence: bs.ver, rec: rec }),
      views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp,
    }
  }

  // Whether pull i may install: its destination still holds the bucket open,
  // at the view it opened before the fetch (fenced), with migration
  // incomplete, and holds no record for the journal (conditional).
  def deliverable(i: int, conditional: bool, fenced: bool): bool = {
    val pl = pulls.get(i)
    val bs = bkt.get(pl.p).get(KEY_BUCKET.get(pl.k))
    and {
      pl.active,
      bs.st == "open",
      not(bs.done),
      not(fenced) or bs.ver == pl.fence,
      not(conditional) or not(isPresent(jr.get(pl.p).get(pl.k))),
    }
  }

  // Second half: delivers pull i in one transaction at its destination. The
  // delivery may be delayed arbitrarily after the fetch.
  action deliverWith(i: int, conditional: bool, fenced: bool): bool = {
    val pl = pulls.get(i)
    all {
      deliverable(i, conditional, fenced),
      jr' = jr.set(pl.p, jr.get(pl.p).set(pl.k, pl.rec)),
      pulls' = pulls.set(i, NO_PULL),
      views' = views, bkt' = bkt, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp,
    }
  }

  action deliver(i: int): bool = deliverWith(i, true, true)

  // The install procedure rejects an undeliverable pull, and the client drops
  // it (and pulls again if it still needs the journal).
  action dropPull(i: int): bool = all {
    pulls.get(i).active,
    not(deliverable(i, true, true)),
    pulls' = pulls.set(i, NO_PULL),
    views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp,
  }

  action markDoneWith(p: int, b: int, early: bool): bool = {
    val bs = bkt.get(p).get(b)
    all {
      bs.st == "open",
      not(bs.done),
      early or keysOf(b).forall(k => isPresent(jr.get(p).get(k))),
      bkt' = bkt.set(p, bkt.get(p).set(b, { ...bs, done: true })),
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp, pulls' = pulls,
    }
  }

  action markDone(p: int, b: int): bool = markDoneWith(p, b, false)

  // Whether a read path can reach partition a's copy of bucket b: a is on the
  // migration chain of the bucket's current opener, or of a partition holding
  // it open. A copy leaves every chain once a later opener on it is done.
  def reachable(a: int, b: int): bool =
    chain(currentOpener(b), b).contains(a)
      or PARTITIONS.exists(p => bkt.get(p).get(b).st == "open" and chain(p, b).contains(a))

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
      views' = views, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp, pulls' = pulls,
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
      views' = views, bkt' = bkt, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp, pulls' = pulls,
    }
  }

  // A journal's mutation history is bounded: a partition evicts the id of a
  // mutation from a record it holds open. Like a tombstone, an id is evicted
  // only once no retry of its op can still arrive (D28); a retry finds its own
  // id, or its op never committed.
  action evictWith(p: int, k: str, id: int, guarded: bool): bool = {
    val r = jr.get(p).get(k)
    all {
      bkt.get(p).get(KEY_BUCKET.get(k)).st == "open",
      r.ids.contains(id),
      not(guarded) or outcome.get(id) != "pending",
      jr' = jr.set(p, jr.get(p).set(k, { ...r, ids: r.ids.exclude(Set(id)) })),
      views' = views, bkt' = bkt, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, nextOp' = nextOp, pulls' = pulls,
    }
  }

  action evict(p: int, k: str, id: int): bool = evictWith(p, k, id, true)

  // The coordinator's rule for committing a view that moves bucket b to p
  // (D11). The previous move of b must have opened (its destination is the
  // current opener), so no partition opens at a superseded view. And no
  // migration chain may reach p's copy of b, if it holds one: p never held b,
  // or a later opener on its chain is done. Otherwise p cannot reopen, since
  // its copy is reclaimable only once the chain's newest opener is done, and
  // that opener is sealed by this move. Moves out of an incomplete migration
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
  action fetchAny = {
    nondet i = PULL_SLOTS.oneOf()
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    fetch(i, p, k)
  }
  action deliverAny = { nondet i = PULL_SLOTS.oneOf() deliver(i) }
  action dropPullAny = { nondet i = PULL_SLOTS.oneOf() dropPull(i) }
  action markDoneAny = { nondet p = PARTITIONS.oneOf() nondet b = BUCKETS.oneOf() markDone(p, b) }
  action reclaimAny = { nondet p = PARTITIONS.oneOf() nondet b = BUCKETS.oneOf() reclaim(p, b) }
  action dropMarkerAny = { nondet p = PARTITIONS.oneOf() nondet k = KEYS.oneOf() dropMarker(p, k) }
  action evictAny = {
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    nondet id = OPS.oneOf()
    evict(p, k, id)
  }

  action protocolStep = any {
    sealAny, openAny, fetchAny, deliverAny, dropPullAny, markDoneAny, reclaimAny, dropMarkerAny, evictAny,
  }

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

  val vars = (views, bkt, jr, truth, ops, outcome, applied, nextOp, pulls)

  temporal fairness = and {
    weakFair(sealAny, vars),
    weakFair(openAny, vars),
    weakFair(fetchAny, vars),
    weakFair(deliverAny, vars),
    weakFair(dropPullAny, vars),
    weakFair(markDoneAny, vars),
    weakFair(reclaimAny, vars),
  }

  // Under weak fairness of sealing, opening, pulling, completion, and
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
    nondet i = PULL_SLOTS.oneOf()
    any { step, deliverWith(i, false, true) }
  }

  // Installs a pulled journal without fencing on the destination's opening, so
  // a delivery delayed past a later reopening installs a stale copy.
  action mutantUnfencedInstall = {
    nondet i = PULL_SLOTS.oneOf()
    any { step, deliverWith(i, true, false) }
  }

  // Evicts a mutation id while a retry of its op can still arrive, as a
  // count-bounded history does.
  action mutantEvictPendingId = {
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    nondet id = OPS.oneOf()
    any { step, evictWith(p, k, id, false) }
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
      views' = views, bkt' = bkt, pulls' = pulls,
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
    MAX_OPS = 5,
    MAX_PULLS = 2
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
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(write(2, "a", false, false, false))
      .then(fetch(0, 2, "b")).then(deliver(0))
      .then(markDone(2, 0))
      .then(reclaim(1, 0))
      .expect(safety and truth.get("a") == List(0, 1))

  // A delete during migration leaves a tombstone, so the journal does not
  // resurrect from the source.
  run deleteDuringMigrationTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
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
      .then(fetch(0, 2, "a")).then(deliver(0))
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
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(fetch(0, 2, "b")).then(deliver(0))
      .then(markDone(2, 0))
      .then(reclaim(1, 0))
      .then(changeView(0, 1))
      .then(seal(2, 0))
      .then(open(1, 0, 2))
      .then(fetch(0, 1, "a")).then(deliver(0))
      .expect(safety and truth.get("a") == List(0))

  // Chained migration: bucket 0 moves 1 -> 2 -> 3 before 2 is done. A pull at 3
  // walks the chain newest first: "a" comes from 2, which holds the newer
  // write, and "b", held by neither, installs as an absent marker. Once 3 is
  // done, no read path reaches 1 or 2, and both reclaim.
  run chainedMigrationTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(write(2, "a", false, false, false))
      .then(changeView(0, 3))
      .then(seal(2, 0))
      .then(open(3, 0, 2))
      .then(fetch(0, 3, "a")).then(deliver(0))
      .then(fetch(0, 3, "b")).then(deliver(0))
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
      .then(fetch(0, 3, "a")).then(deliver(0))
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
      .then(fetch(0, 3, "a")).then(deliver(0))
      .then(fetch(0, 3, "b")).then(deliver(0))
      .then(fetch(0, 3, "c")).then(deliver(0))
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
      .then(fetch(0, 1, "c")).then(deliver(0))
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
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(fetch(0, 2, "b")).then(deliver(0))
      .then(markDone(2, 0))
      .then(changeViewWith(Set(0), 1, true))
      .then(seal(2, 0))
      .then(reclaim(1, 0))
      .then(open(1, 0, 2))
      .expect(safety and openAt(0) == Set(1))

  // Every journal of bucket 0 is in 2, but 2's migration is not done: 1's copy
  // stays on 2's chain, so 1 neither reclaims nor takes the bucket back.
  run reclaimWaitsForDoneTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(fetch(0, 2, "b")).then(deliver(0))
      .then(reclaim(1, 0))
      .fail()

  run returnWaitsForDoneTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(fetch(0, 2, "b")).then(deliver(0))
      .then(changeViewWith(Set(0), 1, true))
      .fail()

  // A pull fetched during 2's first opening of bucket 0 (slot 1) is delayed
  // while the bucket moves 1 -> 2 -> 1 -> 2, gains a write in 1, and 2
  // reclaims and reopens it, empty, from 1.
  run delayedPullSetup =
    moveBucket0
      .then(fetch(1, 2, "a"))
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(fetch(0, 2, "b")).then(deliver(0))
      .then(markDone(2, 0))
      .then(changeView(0, 1))
      .then(seal(2, 0))
      .then(reclaim(1, 0))
      .then(open(1, 0, 2))
      .then(fetch(0, 1, "a")).then(deliver(0))
      .then(write(1, "a", false, false, false))
      .then(fetch(0, 1, "b")).then(deliver(0))
      .then(markDone(1, 0))
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(reclaim(2, 0))
      .then(open(2, 0, 3))

  // The install is fenced on the opening at which 2 fetched, so the delayed
  // pull is rejected and dropped, and 2 pulls the current journal.
  run delayedPullFencedTest =
    delayedPullSetup
      .then(dropPull(1))
      .then(fetch(0, 2, "a")).then(deliver(0))
      .expect(safety and jr.get(2).get("a").vals == List(0, 1))

  run delayedPullRejectedTest =
    delayedPullSetup
      .then(deliver(1))
      .fail()

  // A mutation id is evicted only once no retry of its op can arrive.
  run evictAfterAckTest =
    init
      .then(write(1, "a", false, false, false))
      .then(write(1, "a", false, false, false))
      .then(evict(1, "a", 0))
      .expect(safety and jr.get(1).get("a").ids == Set(1))

  run evictPendingRejectedTest =
    init
      .then(write(1, "a", false, false, true))
      .then(evict(1, "a", 0))
      .fail()
  run deleteWithoutTombstoneResurrectsTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(deleteWithoutTombstone(2, "a"))
      .expect(not(safety))

  run unconditionalInstallLosesWriteTest =
    moveBucket0
      .then(fetch(0, 2, "a"))
      .then(fetch(1, 2, "a"))
      .then(deliver(1))
      .then(write(2, "a", false, false, false))
      .then(deliverWith(0, false, true))
      .expect(not(safety))

  run reclaimBeforeDoneTest =
    moveBucket0
      .then(reclaimWith(1, 0, true))
      .expect(not(safety))

  // Without the fence, the delayed pull installs the copy from 2's first
  // opening and hides the write committed in 1.
  run unfencedInstallHidesWriteTest =
    delayedPullSetup
      .then(deliverWith(1, true, false))
      .expect(not(safety))

  // A create whose reply was lost is followed by an append and a delete. With
  // its id evicted, the retry finds the journal absent again and recreates it.
  run evictPendingIdRecreatesTest =
    init
      .then(write(1, "a", false, false, true))
      .then(write(1, "a", false, false, false))
      .then(write(1, "a", true, false, false))
      .then(evictWith(1, "a", 0, false))
      .then(retry(1, 0, false))
      .expect(not(safety))

  // Without the deletion, the retry of the committed op reports a conflict.
  run evictPendingIdConflictTest =
    init
      .then(write(1, "a", false, false, true))
      .then(write(1, "a", false, false, false))
      .then(evictWith(1, "a", 0, false))
      .then(retry(1, 0, false))
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
      .then(fetch(0, 2, "a")).then(deliver(0))
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
    MAX_OPS = 2,
    MAX_PULLS = 1
  ).*
}

// Configuration for checking liveness with TLC.
module garnetRangeMigrationLive {
  import garnetRangeMigration(
    PARTITIONS = Set(1, 2, 3),
    KEYS = Set("a", "c"),
    KEY_BUCKET = Map("a" -> 0, "c" -> 1),
    MAX_VIEWS = 3,
    MAX_OPS = 1,
    MAX_PULLS = 1
  ).*
}
