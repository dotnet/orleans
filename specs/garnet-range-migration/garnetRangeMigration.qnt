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
//   id), an identity (the ETag of the mutation that wrote it, which never
//   recurs; 0 for no record), and the ids of its last RECENT mutations (the
//   recent-ETag list). Tombstones and absent markers are records that read as
//   absent; a tombstone's identity names the absence it represents.
// - Every write is conditional on a state identity: the live journal the
//   client observed, or the absence it observed, named by its identity
//   (D16). A retry whose id has left the history fails its condition, and
//   the client reports the outcome as unknown (`ambiguous`).
// - Any reply can be lost; the client later retries the same op. An attempt
//   whose reply was lost may also execute again later (a queued or delayed
//   duplicate), until it expires.
// - Pulls (on NeedPull) and consolidation are two requests: a fetch from the
//   bucket's migration chain, and a later install, fenced by the opening the
//   destination fetched for, if the destination holds no record (D43).
// - A view change moves one or more buckets to one partition, which covers
//   moves, splits, and merges. Requests carry no view version (D17), so
//   clients may send any operation to any partition. Views change by the
//   coordinator's rule (`mayMove`), which allows chained migrations through
//   partitions off the chain. An open is authorized by observing the source's
//   seal, and applied later at the destination, fenced by the destination's
//   own bucket version (D45). `uncoordinatedStep` changes views freely; there,
//   a delayed open can leave two partitions open, and migrations can stall.
module garnetRangeMigration {

  // ---------------------------------------------------------------- Types

  type JRec = { kind: str, vals: List[int], ver: int, ids: List[int] }

  // st: "absent" | "open" | "sealed". ver: view at which the partition opened
  // the bucket. sealVer: view whose change sealed it. src: partition holding
  // the bucket's earlier history (-1: none).
  type Bucket = { st: str, ver: int, sealVer: int, src: int, done: bool, reclaimed: bool }

  // live: whether the condition names a live journal or an absence. expect:
  // the identity of the state the client observed.
  type Op = { key: str, del: bool, live: bool, expect: int }

  type Walk = { q: int, finished: bool, err: bool, rec: JRec }

  // The state a client observed: a live journal or an absence, and its
  // identity.
  type Obs = { live: bool, ver: int }

  // An open in flight: partition m may open bucket b at view v, with src as
  // the source of its history. Authorized once the source's seal is observed,
  // and applied later at m.
  type Ticket = { active: bool, m: int, b: int, v: int, src: int }

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
  const MAX_OPENS: int
  // Length of each journal's mutation history.
  const RECENT: int

  pure val BUCKETS = Set(0, 1)
  pure val DEPTH = MAX_VIEWS + 2
  pure val OPS = 0.to(MAX_OPS - 1)
  pure val PULL_SLOTS = 0.to(MAX_PULLS - 1)
  pure val OPEN_SLOTS = 0.to(MAX_OPENS - 1)

  pure val NONE: JRec = { kind: "none", vals: List(), ver: 0, ids: List() }
  pure val ABSENT_MARKER: JRec = { kind: "absent", vals: List(), ver: 0, ids: List() }
  pure val NO_BUCKET: Bucket = { st: "absent", ver: -1, sealVer: -1, src: -1, done: false, reclaimed: false }
  pure val NO_PULL: Pull = { active: false, p: -1, k: "", fence: -1, rec: NONE }
  pure val NO_TICKET: Ticket = { active: false, m: -1, b: -1, v: -1, src: -1 }

  pure def keysOf(b: int): Set[str] = KEYS.filter(k => KEY_BUCKET.get(k) == b)
  pure def isLive(r: JRec): bool = r.kind == "live"
  pure def isPresent(r: JRec): bool = r.kind != "none"
  pure def has(l: List[int], x: int): bool = l.foldl(false, (acc, y) => acc or y == x)

  // The version of a partition's last transition of a bucket: its opening or
  // its seal (-1 if it never held the bucket). Reclamation keeps it.
  pure def localVersion(bs: Bucket): int =
    if (bs.st == "absent") -1 else if (bs.sealVer > bs.ver) bs.sealVer else bs.ver

  // Appends a mutation id to a history, evicting the oldest beyond RECENT.
  pure def remember(l: List[int], id: int): List[int] = {
    val l2 = l.append(id)
    if (l2.length() > RECENT) l2.slice(l2.length() - RECENT, l2.length()) else l2
  }

  // ------------------------------------------------------------ State

  var views: List[int -> int]
  var bkt: int -> (int -> Bucket)
  var jr: int -> (str -> JRec)
  // Oracle: each journal's content after every committed write.
  var truth: str -> List[int]
  var ops: int -> Op
  // "none" | "pending" (reply lost) | "acked" | "conflict" | "ambiguous"
  var outcome: int -> str
  var applied: int -> int
  // Oracle: the ops committed on each journal, in order.
  var log: str -> List[int]
  var nextOp: int
  var pulls: int -> Pull
  // Every state each journal has had, which a client may have observed.
  var seen: str -> Set[Obs]
  // Whether an attempt of each op may still execute: one whose reply was
  // lost, until it expires.
  var inflight: int -> bool
  var opens: int -> Ticket

  // ------------------------------------------------------------ Derived state

  // The partition that most recently opened the bucket.
  def currentOpener(b: int): int =
    PARTITIONS.fold(-1, (acc, p) =>
      if (bkt.get(p).get(b).st == "absent") acc
      else if (acc < 0) p
      else if (bkt.get(p).get(b).ver > bkt.get(acc).get(b).ver) p
      else acc)

  def openAt(b: int): Set[int] = PARTITIONS.filter(p => bkt.get(p).get(b).st == "open")

  // The version which assigned bucket b to partition m in the latest view: the
  // first view of the latest run of views in which m owns b (-1 if m does not
  // own b in the latest view).
  def assignedAt(b: int, m: int): int =
    range(0, MAX_VIEWS).foldl(-1, (acc, w) =>
      if (w >= views.length()) acc
      else if (views[w].get(b) != m) -1
      else if (acc < 0) w
      else acc)

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

  // Applies op `id` to record r. Returns the new record and the outcome. A
  // retry that fails its condition reports the outcome as unknown, since an
  // earlier attempt may have committed and left the history.
  pure def apply(r: JRec, id: int, op: Op, checkIds: bool, isRetry: bool, anyAbsence: bool): { rec: JRec, status: str, commit: bool } =
    if (checkIds and has(r.ids, id)) { rec: r, status: "acked", commit: false }
    else {
      val ok =
        if (op.live) isLive(r) and r.ver == op.expect
        else not(isLive(r)) and (anyAbsence or r.ver == op.expect)
      if (not(ok) or (op.del and not(isLive(r)))) { rec: r, status: if (isRetry) "ambiguous" else "conflict", commit: false }
      else if (op.del) { rec: { kind: "tomb", vals: List(), ver: id + 1, ids: remember(r.ids, id) }, status: "acked", commit: true }
      else {
        val base = if (isLive(r)) r.vals else List()
        { rec: { kind: "live", vals: base.append(id), ver: id + 1, ids: remember(r.ids, id) }, status: "acked", commit: true }
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
    ops' = OPS.mapBy(_ => { key: "", del: false, live: false, expect: 0 }),
    outcome' = OPS.mapBy(_ => "none"),
    applied' = OPS.mapBy(_ => 0),
    log' = KEYS.mapBy(_ => List()),
    nextOp' = 0,
    pulls' = PULL_SLOTS.mapBy(_ => NO_PULL),
    seen' = KEYS.mapBy(_ => Set({ live: false, ver: 0 })),
    inflight' = OPS.mapBy(_ => false),
    opens' = OPEN_SLOTS.mapBy(_ => NO_TICKET),
  }

  // Runs op `id` (already recorded in `opsNext`) at partition p, which must
  // hold the bucket open (the in-band seal check) and hold the journal or have
  // completed migration (otherwise the partition answers NeedPull and the
  // client pulls first). A lost reply leaves the outcome pending, and the
  // attempt may execute again later. A delayed attempt changes no outcome:
  // the client has its outcome already, or retries.
  action executeWith(p: int, id: int, op: Op, checkIds: bool, isRetry: bool, anyAbsence: bool, sealCheck: bool, lost: bool, delayed: bool): bool = {
    val b = KEY_BUCKET.get(op.key)
    val bs = bkt.get(p).get(b)
    val r = jr.get(p).get(op.key)
    val res = apply(r, id, op, checkIds, isRetry, anyAbsence)
    all {
      if (sealCheck) bs.st == "open" else bs.st != "absent",
      isPresent(r) or bs.done,
      jr' = jr.set(p, jr.get(p).set(op.key, res.rec)),
      truth' = if (res.commit) truth.set(op.key, res.rec.vals) else truth,
      applied' = if (res.commit) applied.set(id, applied.get(id) + 1) else applied,
      log' = if (res.commit) log.set(op.key, log.get(op.key).append(id)) else log,
      seen' = if (res.commit) seen.set(op.key, seen.get(op.key).union(Set({ live: isLive(res.rec), ver: res.rec.ver }))) else seen,
      outcome' = if (delayed) outcome else outcome.set(id, if (lost) "pending" else res.status),
      inflight' = if (lost) inflight.set(id, true) else inflight,
    }
  }

  def current(p: int, k: str): Obs = { live: isLive(jr.get(p).get(k)), ver: jr.get(p).get(k).ver }

  // A client issues a new write, conditional on a state it observed: the
  // current one, or any earlier one (a duplicate activation, or a client
  // holding an old observation).
  action writeWith(p: int, k: str, del: bool, obs: Obs, lost: bool, sealCheck: bool): bool = {
    val id = nextOp
    val op = { key: k, del: del, live: obs.live, expect: obs.ver }
    all {
      nextOp < MAX_OPS,
      ops' = ops.set(id, op),
      nextOp' = nextOp + 1,
      executeWith(p, id, op, true, false, false, sealCheck, lost, false),
      views' = views, bkt' = bkt, pulls' = pulls, opens' = opens,
    }
  }

  action write(p: int, k: str, del: bool, lost: bool): bool = writeWith(p, k, del, current(p, k), lost, true)

  // A client retries an op whose reply was lost, with identical arguments, at
  // whichever partition it routes to.
  action retryWith(p: int, id: int, checkIds: bool, anyAbsence: bool, lost: bool): bool = all {
    outcome.get(id) == "pending",
    executeWith(p, id, ops.get(id), checkIds, true, anyAbsence, true, lost, false),
    views' = views, bkt' = bkt, ops' = ops, nextOp' = nextOp, pulls' = pulls, opens' = opens,
  }

  action retry(p: int, id: int, lost: bool): bool = retryWith(p, id, true, false, lost)

  // A delayed attempt of an op executes after the client has its outcome.
  action deliverAttempt(p: int, id: int): bool = all {
    inflight.get(id),
    outcome.get(id) != "pending",
    executeWith(p, id, ops.get(id), true, true, false, true, false, true),
    views' = views, bkt' = bkt, ops' = ops, nextOp' = nextOp, pulls' = pulls, opens' = opens,
  }

  // An attempt expires once its deadline passes: no partition executes it
  // after that (D41). The client has stopped retrying the op.
  action expire(id: int): bool = all {
    inflight.get(id),
    outcome.get(id) != "pending",
    inflight' = inflight.set(id, false),
    views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, opens' = opens,
  }

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
      bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens,
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
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens,
    }
  }

  // An open of bucket b at partition m for view v is authorized once the
  // bucket's current opener has sealed it, as the coordinator records in the
  // migration's `Sealed` phase. The migration is the latest map's: m owns b
  // there, and v is the version which assigned it. The authorization is
  // recorded in open slot i and applied later, possibly more than once
  // (duplicate requests), after the map has moved on.
  action authorizeWith(i: int, m: int, b: int, v: int, requireSeal: bool): bool = {
    val cur = currentOpener(b)
    val curState = bkt.get(cur).get(b)
    all {
      not(opens.get(i).active),
      cur >= 0,
      v == assignedAt(b, m),
      v > curState.ver,
      not(requireSeal) or curState.st == "sealed",
      opens' = opens.set(i, { active: true, m: m, b: b, v: v, src: cur }),
      views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight,
    }
  }

  action authorize(i: int, m: int, b: int, v: int): bool = authorizeWith(i, m, b, v, true)

  // Whether open i applies at its partition, in one transaction there. Fenced:
  // its view is later than the partition's last transition of the bucket
  // (D45). Reopen rule (D11): the partition holds no copy of the bucket, or
  // has reclaimed it, or is itself the source and keeps its history.
  def openable(i: int, fenced: bool): bool = {
    val t = opens.get(i)
    val mine = bkt.get(t.m).get(t.b)
    and {
      t.active,
      mine.st != "open",
      not(fenced) or t.v > localVersion(mine),
      (t.src == t.m and not(mine.reclaimed)) or mine.st == "absent" or mine.reclaimed,
    }
  }

  action openTicketWith(i: int, fenced: bool): bool = {
    val t = opens.get(i)
    val mine = bkt.get(t.m).get(t.b)
    val opened =
      if (t.src == t.m) { ...mine, st: "open", ver: t.v, sealVer: -1 }
      else { st: "open", ver: t.v, sealVer: -1, src: t.src, done: false, reclaimed: false }
    all {
      openable(i, fenced),
      bkt' = bkt.set(t.m, bkt.get(t.m).set(t.b, opened)),
      opens' = opens.set(i, NO_TICKET),
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight,
    }
  }

  action openTicket(i: int): bool = openTicketWith(i, true)

  // The partition rejects an open that does not apply, and the requester drops
  // it.
  action dropTicket(i: int): bool = all {
    opens.get(i).active,
    not(openable(i, true)),
    opens' = opens.set(i, NO_TICKET),
    views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight,
  }

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
      views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, seen' = seen, inflight' = inflight, opens' = opens,
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
      views' = views, bkt' = bkt, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, seen' = seen, inflight' = inflight, opens' = opens,
    }
  }

  action deliver(i: int): bool = deliverWith(i, true, true)

  // The install procedure rejects an undeliverable pull, and the client drops
  // it (and pulls again if it still needs the journal).
  action dropPull(i: int): bool = all {
    pulls.get(i).active,
    not(deliverable(i, true, true)),
    pulls' = pulls.set(i, NO_PULL),
    views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, seen' = seen, inflight' = inflight, opens' = opens,
  }

  // Whether the migration inventory of bucket b's chain from q, as the
  // consolidator enumerates it, includes journal k: the newest record of k on
  // the chain is a live journal or a tombstone (D42). `liveOnly` enumerates
  // only live journals, as the live index does.
  def inInventory(q: int, k: str, liveOnly: bool): bool = {
    val w = walk(q, k)
    or {
      not(w.finished),
      w.err,
      w.rec.kind == "live",
      not(liveOnly) and w.rec.kind == "tomb",
    }
  }

  // Completes migration once p holds a record of every journal in the
  // inventory. inventory: "full" | "live" (live journals only) | "none".
  action markDoneWith(p: int, b: int, inventory: str): bool = {
    val bs = bkt.get(p).get(b)
    all {
      bs.st == "open",
      not(bs.done),
      inventory == "none" or keysOf(b).forall(k =>
        isPresent(jr.get(p).get(k)) or not(inInventory(bs.src, k, inventory == "live"))),
      bkt' = bkt.set(p, bkt.get(p).set(b, { ...bs, done: true })),
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens,
    }
  }

  action markDone(p: int, b: int): bool = markDoneWith(p, b, "full")

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
      views' = views, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens,
    }
  }

  action reclaim(a: int, b: int): bool = reclaimWith(a, b, false)

  // Drops a tombstone or absent marker once migration is done (D5). A
  // tombstone names the absence and carries the mutation ids that attempts
  // rely on, so it is dropped only once no attempt of an op on the journal can
  // still execute: none awaits a retry, and every lost attempt has expired
  // (D41). An absent marker names the unnamed absence and carries no ids.
  action dropMarkerWith(p: int, k: str, waitForAttempts: bool): bool = {
    val b = KEY_BUCKET.get(k)
    val bs = bkt.get(p).get(b)
    val r = jr.get(p).get(k)
    all {
      bs.st == "open",
      bs.done,
      r.kind == "absent"
        or (r.kind == "tomb" and OPS.forall(id => not(ops.get(id).key == k and (outcome.get(id) == "pending" or (waitForAttempts and inflight.get(id)))))),
      jr' = jr.set(p, jr.get(p).set(k, NONE)),
      views' = views, bkt' = bkt, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens,
    }
  }

  action dropMarker(p: int, k: str): bool = dropMarkerWith(p, k, true)

  // The coordinator's rule for committing a view that moves bucket b to p
  // (D11). The previous move of b must have opened (its destination is the
  // current opener), so no partition opens at a superseded view. And no
  // migration chain may reach p's copy of b, if it holds one: p never held b,
  // or a later opener on its chain is done. Otherwise p cannot reopen, since
  // its copy is reclaimable only once the chain's newest opener is done, and
  // that opener is sealed by this move. Moves out of an incomplete migration
  // (failure-driven moves) therefore chain only through partitions off the
  // chain. Safety also depends on this rule, because opens are applied after
  // they are authorized: it commits no later move of b until the previous
  // move's destination has opened, so no authorized open is superseded.
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
    nondet obs = seen.get(k).oneOf()
    nondet lost = Bool.oneOf()
    any {
      writeWith(p, k, del, obs, lost, true),
      retry(p, id, lost),
      deliverAttempt(p, id),
      expire(id),
    }
  }

  // Partition actions which carry out migrations, each the subject of a
  // fairness condition.
  action sealAny = { nondet p = PARTITIONS.oneOf() nondet b = BUCKETS.oneOf() seal(p, b) }
  action authorizeAny = {
    nondet i = OPEN_SLOTS.oneOf()
    nondet p = PARTITIONS.oneOf()
    nondet b = BUCKETS.oneOf()
    nondet v = 0.to(MAX_VIEWS - 1).oneOf()
    authorize(i, p, b, v)
  }
  action openAny = { nondet i = OPEN_SLOTS.oneOf() openTicket(i) }
  action dropTicketAny = { nondet i = OPEN_SLOTS.oneOf() dropTicket(i) }
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

  action protocolStep = any {
    sealAny, authorizeAny, openAny, dropTicketAny, fetchAny, deliverAny, dropPullAny, markDoneAny, reclaimAny, dropMarkerAny,
  }

  // Views change by the coordinator's rule.
  action step = {
    nondet bs = BUCKETS.powerset().exclude(Set(Set())).oneOf()
    nondet p = PARTITIONS.oneOf()
    any { clientStep, changeViewWith(bs, p, true), protocolStep }
  }

  // As `step`, with views changed freely.
  action uncoordinatedStep = {
    nondet bs = BUCKETS.powerset().exclude(Set(Set())).oneOf()
    nondet p = PARTITIONS.oneOf()
    any { clientStep, changeViews(bs, p), protocolStep }
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
  // as a conflict did not commit. An ambiguous outcome may have committed or
  // not.
  val exactlyOnce = OPS.forall(id => and {
    applied.get(id) <= 1,
    outcome.get(id) == "acked" implies applied.get(id) == 1,
    outcome.get(id) == "conflict" implies applied.get(id) == 0,
  })

  // The number of mutations committed on op id's journal after it.
  def laterMutations(id: int): int = {
    val l = log.get(ops.get(id).key)
    l.indices().fold(0, (n, i) => if (l[i] == id) l.length() - i - 1 else n)
  }

  // A retry reports the committed outcome while fewer than RECENT later
  // mutations of its journal have committed; only then may a committed op
  // report an unknown outcome.
  val retryOutcome = OPS.forall(id =>
    (outcome.get(id) == "ambiguous" and applied.get(id) == 1) implies laterMutations(id) >= RECENT)

  val safety = singleOpen and durability and exactlyOnce and retryOutcome

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

  val vars = (views, bkt, jr, truth, ops, outcome, applied, log, nextOp, pulls, seen, inflight, opens)

  temporal fairness = and {
    weakFair(sealAny, vars),
    weakFair(authorizeAny, vars),
    weakFair(openAny, vars),
    weakFair(dropTicketAny, vars),
    weakFair(fetchAny, vars),
    weakFair(deliverAny, vars),
    weakFair(dropPullAny, vars),
    weakFair(markDoneAny, vars),
    weakFair(reclaimAny, vars),
  }

  // Under weak fairness of sealing, opening, pulling, completion, and
  // reclamation, every migration completes and its sources are reclaimed.
  // Views change finitely often (MAX_VIEWS), so this holds from the last view
  // change on. It requires the coordinator's rule, which `step` follows.
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
    nondet i = OPEN_SLOTS.oneOf()
    any { step, authorizeWith(i, p, b, v, false) }
  }

  // Applies an open without the destination's version fence, so a delayed
  // duplicate reopens a reclaimed bucket at an old view.
  action mutantUnfencedOpen = {
    nondet i = OPEN_SLOTS.oneOf()
    any { step, openTicketWith(i, false) }
  }

  // Completes migration against an inventory of live journals only, missing
  // tombstones whose absence identity and history retried ops rely on.
  action mutantInventoryLiveOnly = {
    nondet p = PARTITIONS.oneOf()
    nondet b = BUCKETS.oneOf()
    any { step, markDoneWith(p, b, "live") }
  }

  // Drops a tombstone once no op awaits a retry, while a lost attempt may
  // still execute.
  action mutantCollectWithAttempt = {
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    any { step, dropMarkerWith(p, k, false) }
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
      ops' = ops.set(id, { key: k, del: true, live: true, expect: r.ver }),
      nextOp' = nextOp + 1,
      jr' = jr.set(p, jr.get(p).set(k, NONE)),
      truth' = truth.set(k, List()),
      applied' = applied.set(id, 1),
      log' = log.set(k, log.get(k).append(id)),
      outcome' = outcome.set(id, "acked"),
      views' = views, bkt' = bkt, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens,
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
    any { step, markDoneWith(p, b, "none") }
  }

  // Retries without recognizing the op's own committed mutation.
  action mutantRetryWithoutIdempotence = {
    nondet p = PARTITIONS.oneOf()
    nondet id = OPS.oneOf()
    nondet lost = Bool.oneOf()
    any { step, retryWith(p, id, false, false, lost) }
  }

  // Retries match any absence instead of the absence the op observed, so a
  // creation whose id left the history recreates a deleted journal.
  action mutantRetryAnyAbsence = {
    nondet p = PARTITIONS.oneOf()
    nondet id = OPS.oneOf()
    nondet lost = Bool.oneOf()
    any { step, retryWith(p, id, true, true, lost) }
  }

  // Acknowledges a stale writer's write in a sealed bucket (the Delos erratum).
  action mutantWriteAfterSeal = {
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    nondet del = Bool.oneOf()
    any { step, writeWith(p, k, del, current(p, k), false, false) }
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
    MAX_PULLS = 2,
    MAX_OPENS = 2,
    RECENT = 2
  ).*

  // Moves bucket 0 from partition 1 to 2.
  run moveBucket0 =
    init
      .then(write(1, "a", false, false))
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))

  // Pulls, writes, consolidates, completes, and reclaims the source.
  run handoverTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(write(2, "a", false, false))
      .then(fetch(0, 2, "b")).then(deliver(0))
      .then(markDone(2, 0))
      .then(reclaim(1, 0))
      .expect(safety and truth.get("a") == List(0, 1))

  // A delete during migration leaves a tombstone, so the journal does not
  // resurrect from the source.
  run deleteDuringMigrationTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(write(2, "a", true, false))
      .expect(safety and truth.get("a") == List())

  // A write whose reply was lost is retried at the new owner after migration
  // and reports success without applying twice.
  run retryAcrossMigrationTest =
    init
      .then(write(1, "a", false, true))
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(retry(2, 0, false))
      .expect(safety and outcome.get(0) == "acked")

  // A stale writer's write in the sealed source is rejected.
  run staleWriterRejectedTest =
    moveBucket0
      .then(write(1, "a", false, false))
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
      .then(authorize(0, 1, 0, 2)).then(openTicket(0))
      .then(fetch(0, 1, "a")).then(deliver(0))
      .expect(safety and truth.get("a") == List(0))

  // Chained migration: bucket 0 moves 1 -> 2 -> 3 before 2 is done. A pull at 3
  // walks the chain newest first: "a" comes from 2, which holds the newer
  // write, and "b", held by neither, installs as an absent marker. Once 3 is
  // done, no read path reaches 1 or 2, and both reclaim.
  run chainedMigrationTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(write(2, "a", false, false))
      .then(changeView(0, 3))
      .then(seal(2, 0))
      .then(authorize(0, 3, 0, 2)).then(openTicket(0))
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
      .then(authorize(0, 3, 0, 2)).then(openTicket(0))
      .then(fetch(0, 3, "a")).then(deliver(0))
      .expect(safety and jr.get(3).get("a").vals == List(0))

  // While a later opener's migration is incomplete, the chain still reaches
  // the earlier copies, so they are not reclaimed.
  run chainPinsSourcesTest =
    moveBucket0
      .then(changeView(0, 3))
      .then(seal(2, 0))
      .then(authorize(0, 3, 0, 2)).then(openTicket(0))
      .then(reclaim(1, 0))
      .fail()

  // Merge: one view moves bucket 0 (from 1) and bucket 1 (from 2) to 3.
  run mergeTest =
    init
      .then(write(1, "a", false, false))
      .then(write(2, "c", false, false))
      .then(changeViews(Set(0, 1), 3))
      .then(seal(1, 0))
      .then(seal(2, 1))
      .then(authorize(0, 3, 0, 1)).then(openTicket(0))
      .then(authorize(0, 3, 1, 1)).then(openTicket(0))
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
      .then(authorize(0, 1, 1, 2)).then(openTicket(0))
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
      .then(authorize(0, 1, 0, 2)).then(openTicket(0))
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
      .then(authorize(0, 1, 0, 2)).then(openTicket(0))
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
      .then(authorize(0, 1, 0, 2)).then(openTicket(0))
      .then(fetch(0, 1, "a")).then(deliver(0))
      .then(write(1, "a", false, false))
      .then(fetch(0, 1, "b")).then(deliver(0))
      .then(markDone(1, 0))
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(reclaim(2, 0))
      .then(authorize(0, 2, 0, 3)).then(openTicket(0))

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

  // A retry whose id is still in the history reports the committed outcome.
  run retryWithinHistoryTest =
    init
      .then(write(1, "a", false, true))
      .then(write(1, "a", false, false))
      .then(retry(1, 0, false))
      .expect(safety and outcome.get(0) == "acked")

  // After RECENT later mutations, the retry's id has left the history; its
  // condition names a state that never recurs, so it fails and reports an
  // unknown outcome without applying again.
  run retryAfterEvictionTest =
    init
      .then(write(1, "a", false, true))
      .then(write(1, "a", false, false))
      .then(write(1, "a", false, false))
      .then(retry(1, 0, false))
      .expect(safety and outcome.get(0) == "ambiguous" and applied.get(0) == 1)

  // A creation whose reply was lost, followed by an append and a delete: the
  // retry's condition names the original absence, not the tombstone's, so it
  // does not recreate the journal.
  run retryCreateAfterDeleteTest =
    init
      .then(write(1, "a", false, true))
      .then(write(1, "a", false, false))
      .then(write(1, "a", true, false))
      .then(retry(1, 0, false))
      .expect(safety and outcome.get(0) == "ambiguous" and truth.get("a") == List())

  // Partition 2 is authorized to open bucket 0 at view 1, but its open is
  // delayed while view 2 moves the bucket on to 3, which opens from 1. With
  // views changed freely, both partitions open the bucket.
  run delayedOpenUncoordinatedTest =
    init
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 1))
      .then(changeView(0, 3))
      .then(authorize(1, 3, 0, 2))
      .then(openTicket(1))
      .then(openTicket(0))
      .expect(not(singleOpen))

  // The coordinator commits no later move of the bucket until 2 has opened.
  run delayedOpenCoordinatedTest =
    init
      .then(changeViewWith(Set(0), 2, true))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 1))
      .then(changeViewWith(Set(0), 3, true))
      .fail()

  // A duplicate of 2's open at view 1 (slot 1) is delayed while the bucket
  // moves 1 -> 2 -> 1 and 2 reclaims its copy.
  run duplicateOpenSetup =
    init
      .then(write(1, "a", false, false))
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 1))
      .then(authorize(1, 2, 0, 1))
      .then(openTicket(0))
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(markDone(2, 0))
      .then(reclaim(1, 0))
      .then(changeView(0, 1))
      .then(seal(2, 0))
      .then(authorize(0, 1, 0, 2)).then(openTicket(0))
      .then(fetch(0, 1, "a")).then(deliver(0))
      .then(markDone(1, 0))
      .then(reclaim(2, 0))

  // 2's bucket version (its seal at view 2) fences the duplicate out.
  run duplicateOpenFencedTest =
    duplicateOpenSetup
      .then(dropTicket(1))
      .expect(safety and openAt(0) == Set(1))

  run duplicateOpenRejectedTest =
    duplicateOpenSetup
      .then(openTicket(1))
      .fail()

  // A creation's reply is lost, and the journal is deleted before bucket 0
  // migrates.
  run lostCreateDeleteSetup =
    init
      .then(write(1, "a", false, true))
      .then(write(1, "a", true, false))
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))

  // The inventory includes the tombstone, so migration completes only once 2
  // holds it.
  run tombstoneInventoryTest =
    lostCreateDeleteSetup
      .then(markDone(2, 0))
      .fail()

  // The retried creation finds its id in the migrated tombstone and does not
  // apply again.
  run lostCreateDeleteMigrateRetryTest =
    lostCreateDeleteSetup
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(markDone(2, 0))
      .then(reclaim(1, 0))
      .then(retry(2, 0, false))
      .expect(safety and outcome.get(0) == "acked" and truth.get("a") == List())

  // A creation's first attempt is lost and stays queued; the retry succeeds,
  // and the journal is deleted.
  run queuedAttemptSetup =
    init
      .then(write(1, "a", false, true))
      .then(retry(1, 0, false))
      .then(write(1, "a", true, false))

  // The tombstone is kept until the queued attempt expires.
  run collectWaitsForAttemptsTest =
    queuedAttemptSetup
      .then(dropMarker(1, "a"))
      .fail()

  run collectAfterExpiryTest =
    queuedAttemptSetup
      .then(expire(0))
      .then(dropMarker(1, "a"))
      .expect(safety and jr.get(1).get("a").kind == "none")

  // A client observed no journal; another writer then created and deleted it,
  // and its tombstone was collected. The client's creation, conditioned on the
  // unnamed absence, applies: the journal is absent at that point, so the
  // creation is linearizable.
  run staleAbsenceAfterCollectionTest =
    init
      .then(write(1, "a", false, false))
      .then(write(1, "a", true, false))
      .then(dropMarker(1, "a"))
      .then(writeWith(1, "a", false, { live: false, ver: 0 }, false, true))
      .expect(safety and outcome.get(2) == "acked" and truth.get("a") == List(2))

  // A condition on a live journal or a tombstone never matches again.
  run staleLiveConditionRejectedTest =
    init
      .then(write(1, "a", false, false))
      .then(write(1, "a", true, false))
      .then(writeWith(1, "a", false, { live: true, ver: 1 }, false, true))
      .expect(safety and outcome.get(2) == "conflict" and truth.get("a") == List())

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
      .then(write(2, "a", false, false))
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

  // Matching any absence, the retried creation recreates the deleted journal.
  run retryAnyAbsenceRecreatesTest =
    init
      .then(write(1, "a", false, true))
      .then(write(1, "a", false, false))
      .then(write(1, "a", true, false))
      .then(retryWith(1, 0, true, true, false))
      .expect(not(safety))

  // An unfenced duplicate open reopens 2's reclaimed bucket at view 1, while
  // 1 holds it open.
  run unfencedOpenTest =
    duplicateOpenSetup
      .then(openTicketWith(1, false))
      .expect(not(safety))

  // Completing migration against the live index misses the tombstone, so the
  // retried creation applies again in 2.
  run inventoryLiveOnlyResurrectsTest =
    lostCreateDeleteSetup
      .then(markDoneWith(2, 0, "live"))
      .then(retry(2, 0, false))
      .expect(not(safety))

  // Collecting the tombstone while the creation's first attempt is queued lets
  // that attempt apply again.
  run collectWithAttemptResurrectsTest =
    queuedAttemptSetup
      .then(dropMarkerWith(1, "a", false))
      .then(deliverAttempt(1, 0))
      .expect(not(safety))

  // Without the history, the retry of the committed op reports an unknown
  // outcome although no later mutation evicted its id.
  run retryWithoutIdempotenceTest =
    init
      .then(write(1, "a", false, true))
      .then(retryWith(1, 0, false, false, false))
      .expect(not(safety))

  // Once the destination has pulled, a write acknowledged in the sealed source
  // is lost.
  run writeAfterSealTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(writeWith(1, "a", false, current(1, "a"), false, false))
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
    MAX_PULLS = 1,
    MAX_OPENS = 1,
    RECENT = 1
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
    MAX_PULLS = 1,
    MAX_OPENS = 1,
    RECENT = 1
  ).*
}
