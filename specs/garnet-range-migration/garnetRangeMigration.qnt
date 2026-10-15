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
// - Once migration is done, a partition drops tombstones and absent markers,
//   whatever attempts are outstanding. A deletion stamps its tombstone with
//   the bucket's collection generation, and dropping the tombstone raises the
//   generation past the stamp. A write conditioned on the empty absence
//   carries the generation observed with it and fails if the generation has
//   since passed it (D25). The partitions holding the bucket assign
//   generations; no client clock enters.
// - Pulls (on NeedPull) and consolidation are two requests: a fetch from the
//   bucket's migration chain, and a later install, fenced by the opening the
//   destination fetched for, if the destination holds no record (D43).
// - Completion and reclamation are also two requests each, which may be
//   delayed and duplicated: completion is fenced by the destination's opening
//   (D46), and every reclamation batch by the source's seal version (D47).
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

  // dgen: for a tombstone, the bucket's collection generation when its
  // deletion committed; 0 otherwise.
  type JRec = { kind: str, vals: List[int], ver: int, ids: List[int], dgen: int }

  // st: "absent" | "open" | "sealed". ver: view at which the partition opened
  // the bucket. sealVer: view whose change sealed it. src: partition holding
  // the bucket's earlier history (-1: none). gen: the collection generation,
  // one more than the greatest stamp (dgen) of a tombstone which this copy, or
  // a copy before it on the bucket's history, dropped (0: none).
  // Key filters (D36): `of` is the filter a partition writes when it seals the
  // bucket, a superset of the journals it holds a record of (live journals,
  // tombstones, and absent markers: the migration inventory), together with
  // its own chain's filter while its migration is not done. `cf` is the filter
  // of the chain behind this tenure, copied from the source's `of` when the
  // partition opened the bucket. Filters may report false positives; no
  // filter, or an unknown one, is the filter which reports every key (KEYS).
  type Bucket = { st: str, ver: int, sealVer: int, src: int, done: bool, reclaimed: bool, gen: int, of: Set[str], cf: Set[str] }

  // live: whether the condition names a live journal or an absence. expect:
  // the identity of the state the client observed. obs: the collection
  // generation observed with it.
  type Op = { key: str, del: bool, live: bool, expect: int, obs: int }

  type Walk = { q: int, finished: bool, err: bool, rec: JRec }

  // The state a client observed: a live journal or an absence, its identity,
  // and the bucket's collection generation.
  type Obs = { live: bool, ver: int, gen: int }

  // An open in flight: partition m may open bucket b at view v, with src as
  // the source of its history and gen as its collection generation, which is
  // final once the source is sealed, and cf as the key filter the sealed
  // source wrote, which is final too. Authorized once the source's seal is
  // observed, and applied later at m.
  type Ticket = { active: bool, m: int, b: int, v: int, src: int, gen: int, cf: Set[str] }

  // A completion in flight: p may set bucket b `done` once it holds a record
  // of every journal in inv, the migration inventory enumerated while p held
  // the bucket open at view fence.
  type DoneReq = { active: bool, p: int, b: int, fence: int, inv: Set[str] }

  // A reclamation in flight: a may delete its copy of bucket b, which it
  // sealed at version ver.
  type ReclaimReq = { active: bool, a: int, b: int, ver: int }

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
  const MAX_DONES: int
  const MAX_RECLAIMS: int
  // Length of each journal's mutation history.
  const RECENT: int
  // Whether each seal chooses to write a key filter and the filter's false
  // positives. Otherwise every seal writes an exact filter: sealed copies never
  // change, so exact filters add little state, which keeps exhaustive checking
  // (the TLC configurations) tractable. Without filters the protocol is the
  // one checked before key filters were added.
  const FILTER_CHOICES: bool

  pure val BUCKETS = Set(0, 1)
  pure val DEPTH = MAX_VIEWS + 2
  pure val OPS = 0.to(MAX_OPS - 1)
  pure val PULL_SLOTS = 0.to(MAX_PULLS - 1)
  pure val OPEN_SLOTS = 0.to(MAX_OPENS - 1)
  pure val DONE_SLOTS = 0.to(MAX_DONES - 1)
  pure val RECLAIM_SLOTS = 0.to(MAX_RECLAIMS - 1)

  pure val NONE: JRec = { kind: "none", vals: List(), ver: 0, ids: List(), dgen: 0 }
  pure val ABSENT_MARKER: JRec = { kind: "absent", vals: List(), ver: 0, ids: List(), dgen: 0 }
  pure val NO_BUCKET: Bucket = { st: "absent", ver: -1, sealVer: -1, src: -1, done: false, reclaimed: false, gen: 0, of: KEYS, cf: KEYS }
  pure val NO_PULL: Pull = { active: false, p: -1, k: "", fence: -1, rec: NONE }
  pure val NO_TICKET: Ticket = { active: false, m: -1, b: -1, v: -1, src: -1, gen: 0, cf: KEYS }
  pure val NO_DONE: DoneReq = { active: false, p: -1, b: -1, fence: -1, inv: Set() }
  pure val NO_RECLAIM: ReclaimReq = { active: false, a: -1, b: -1, ver: -1 }

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
  var dones: int -> DoneReq
  var reclaims: int -> ReclaimReq
  // Oracle: for each journal, the latest deletion whose tombstone a partition
  // dropped (-1: none).
  var collected: str -> int

  // ------------------------------------------------------------ Derived state

  // The partition that most recently opened the bucket.
  def currentOpener(b: int): int =
    PARTITIONS.fold(-1, (acc, p) =>
      if (bkt.get(p).get(b).st == "absent") acc
      else if (acc < 0) p
      else if (bkt.get(p).get(b).ver > bkt.get(acc).get(b).ver) p
      else acc)

  def openAt(b: int): Set[int] = PARTITIONS.filter(p => bkt.get(p).get(b).st == "open")

  // Whether partition q has finished reclaiming its copy of bucket b: the
  // bucket is marked reclaimed and no record of it remains.
  def cleared(q: int, b: int): bool =
    bkt.get(q).get(b).reclaimed and keysOf(b).forall(k => not(isPresent(jr.get(q).get(k))))

  // The version which assigned bucket b to partition m in the latest view: the
  // first view of the latest run of views in which m owns b (-1 if m does not
  // own b in the latest view).
  def assignedAt(b: int, m: int): int =
    range(0, MAX_VIEWS).foldl(-1, (acc, w) =>
      if (w >= views.length()) acc
      else if (views[w].get(b) != m) -1
      else if (acc < 0) w
      else acc)

  // Whether partition p answers for journal k without a pull: it holds no
  // record of k, and its chain's key filter reports that no partition on the
  // chain holds one (D36). The journal then reads as the empty absence at the
  // bucket's collection generation, exactly as an absent marker installed by a
  // pull would.
  def filteredAbsent(p: int, k: str): bool = not(bkt.get(p).get(KEY_BUCKET.get(k)).cf.contains(k))

  // Reads key k through the migration chain starting at partition q0, as a
  // pull does. Reading a reclaimed bucket is an error, never absence. With
  // `useFilter`, q0 answers from its chain's key filter for a journal it holds
  // no record of, as a partition does before answering NeedPull; pulls read
  // every link.
  def walkWith(q0: int, k: str, useFilter: bool): Walk = {
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
          else if (useFilter and s.q == q0 and filteredAbsent(s.q, k)) { ...s, finished: true }
          else { ...s, q: bs.src }
        })
  }

  def walk(q0: int, k: str): Walk = walkWith(q0, k, false)

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
    val w = walkWith(p, k, true)
    { err: w.err or not(w.finished), vals: w.rec.vals }
  }

  // Applies op `id` to record r in a bucket copy with collection generation
  // gen; a deletion stamps its tombstone with `stamp`. Returns the new record
  // and the outcome. A retry that fails its condition reports the outcome as
  // unknown, since an earlier attempt may have committed and left the history.
  // The empty absence (no record, or an absent marker) fails every condition
  // observed at an earlier generation: a dropped tombstone, of a deletion at or
  // after the observation, may have held the op's own commitment (D25).
  pure def apply(r: JRec, id: int, op: Op, checkIds: bool, isRetry: bool, anyAbsence: bool, gen: int, stamp: int): { rec: JRec, status: str, commit: bool } =
    if (checkIds and has(r.ids, id)) { rec: r, status: "acked", commit: false }
    else {
      val ok =
        if (op.live) isLive(r) and r.ver == op.expect
        else not(isLive(r)) and (anyAbsence or r.ver == op.expect) and not(r.ver == 0 and op.obs < gen)
      if (not(ok) or (op.del and not(isLive(r)))) { rec: r, status: if (isRetry) "ambiguous" else "conflict", commit: false }
      else if (op.del) { rec: { kind: "tomb", vals: List(), ver: id + 1, ids: remember(r.ids, id), dgen: stamp }, status: "acked", commit: true }
      else {
        val base = if (isLive(r)) r.vals else List()
        { rec: { kind: "live", vals: base.append(id), ver: id + 1, ids: remember(r.ids, id), dgen: 0 }, status: "acked", commit: true }
      }
    }

  // ------------------------------------------------------------ Actions

  action init = all {
    views' = List(Map(0 -> 1, 1 -> 2)),
    bkt' = PARTITIONS.mapBy(p => BUCKETS.mapBy(b =>
      if ((p == 1 and b == 0) or (p == 2 and b == 1))
        { ...NO_BUCKET, st: "open", ver: 0, done: true }
      else NO_BUCKET)),
    jr' = PARTITIONS.mapBy(_ => KEYS.mapBy(_ => NONE)),
    truth' = KEYS.mapBy(_ => List()),
    ops' = OPS.mapBy(_ => { key: "", del: false, live: false, expect: 0, obs: 0 }),
    outcome' = OPS.mapBy(_ => "none"),
    applied' = OPS.mapBy(_ => 0),
    log' = KEYS.mapBy(_ => List()),
    nextOp' = 0,
    pulls' = PULL_SLOTS.mapBy(_ => NO_PULL),
    seen' = KEYS.mapBy(_ => Set({ live: false, ver: 0, gen: 0 })),
    inflight' = OPS.mapBy(_ => false),
    opens' = OPEN_SLOTS.mapBy(_ => NO_TICKET),
    dones' = DONE_SLOTS.mapBy(_ => NO_DONE),
    reclaims' = RECLAIM_SLOTS.mapBy(_ => NO_RECLAIM),
    collected' = KEYS.mapBy(_ => -1),
  }

  // Runs op `id` (already recorded in `opsNext`) at partition p, which must
  // hold the bucket open (the in-band seal check) and hold the journal, have
  // completed migration, or have a key filter which reports that its chain
  // holds no record of the journal (otherwise the partition answers NeedPull
  // and the client pulls first). A lost reply leaves the outcome pending, and the
  // attempt may execute again later. A delayed attempt changes no outcome:
  // the client has its outcome already, or retries. A deletion stamps its
  // tombstone with the bucket's collection generation, or, with `clientStamp`
  // (a mutant), with the time the client supplied.
  action executeWith(p: int, id: int, op: Op, checkIds: bool, isRetry: bool, anyAbsence: bool, sealCheck: bool, lost: bool, delayed: bool, clientStamp: bool): bool = {
    val b = KEY_BUCKET.get(op.key)
    val bs = bkt.get(p).get(b)
    val r = jr.get(p).get(op.key)
    val res = apply(r, id, op, checkIds, isRetry, anyAbsence, bs.gen, if (clientStamp) op.obs else bs.gen)
    all {
      if (sealCheck) bs.st == "open" else bs.st != "absent",
      isPresent(r) or bs.done or filteredAbsent(p, op.key),
      jr' = jr.set(p, jr.get(p).set(op.key, res.rec)),
      truth' = if (res.commit) truth.set(op.key, res.rec.vals) else truth,
      applied' = if (res.commit) applied.set(id, applied.get(id) + 1) else applied,
      log' = if (res.commit) log.set(op.key, log.get(op.key).append(id)) else log,
      seen' = if (res.commit) seen.set(op.key, seen.get(op.key).union(Set({ live: isLive(res.rec), ver: res.rec.ver, gen: bs.gen }))) else seen,
      outcome' = if (delayed) outcome else outcome.set(id, if (lost) "pending" else res.status),
      inflight' = if (lost) inflight.set(id, true) else inflight,
    }
  }

  def current(p: int, k: str): Obs =
    { live: isLive(jr.get(p).get(k)), ver: jr.get(p).get(k).ver, gen: bkt.get(p).get(KEY_BUCKET.get(k)).gen }

  // A client issues a new write, conditional on a state it observed: the
  // current one, or any earlier one (a duplicate activation, or a client
  // holding an old observation), with the collection generation observed with
  // it. With `clock` >= 0 (a mutant), the write carries a client timestamp
  // instead, which also stamps a deletion's tombstone: a horizon of client
  // times.
  action writeWithClock(p: int, k: str, del: bool, obs: Obs, lost: bool, sealCheck: bool, clock: int): bool = {
    val id = nextOp
    val op = { key: k, del: del, live: obs.live, expect: obs.ver, obs: if (clock >= 0) clock else obs.gen }
    all {
      nextOp < MAX_OPS,
      ops' = ops.set(id, op),
      nextOp' = nextOp + 1,
      executeWith(p, id, op, true, false, false, sealCheck, lost, false, clock >= 0),
      views' = views, bkt' = bkt, pulls' = pulls, opens' = opens, collected' = collected, dones' = dones, reclaims' = reclaims,
    }
  }

  action writeWith(p: int, k: str, del: bool, obs: Obs, lost: bool, sealCheck: bool): bool =
    writeWithClock(p, k, del, obs, lost, sealCheck, -1)

  action write(p: int, k: str, del: bool, lost: bool): bool = writeWith(p, k, del, current(p, k), lost, true)

  // A client retries an op whose reply was lost, with identical arguments, at
  // whichever partition it routes to.
  action retryWith(p: int, id: int, checkIds: bool, anyAbsence: bool, lost: bool): bool = all {
    outcome.get(id) == "pending",
    executeWith(p, id, ops.get(id), checkIds, true, anyAbsence, true, lost, false, false),
    views' = views, bkt' = bkt, ops' = ops, nextOp' = nextOp, pulls' = pulls, opens' = opens, collected' = collected, dones' = dones, reclaims' = reclaims,
  }

  action retry(p: int, id: int, lost: bool): bool = retryWith(p, id, true, false, lost)

  // A delayed attempt of an op executes after the client has its outcome.
  action deliverAttempt(p: int, id: int): bool = all {
    inflight.get(id),
    outcome.get(id) != "pending",
    executeWith(p, id, ops.get(id), true, true, false, true, false, true, false),
    views' = views, bkt' = bkt, ops' = ops, nextOp' = nextOp, pulls' = pulls, opens' = opens, collected' = collected, dones' = dones, reclaims' = reclaims,
  }

  // An attempt expires once the client stops retrying and no longer holds it
  // queued. Collection does not wait for expiry: the collection generation
  // rejects an attempt which arrives after its tombstone was dropped (D25).
  action expire(id: int): bool = all {
    inflight.get(id),
    outcome.get(id) != "pending",
    inflight' = inflight.set(id, false),
    views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, opens' = opens, collected' = collected, dones' = dones, reclaims' = reclaims,
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
      views' = views.append(bs.fold(last, (m, b) => m.set(b, p))), collected' = collected,
      bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens, dones' = dones, reclaims' = reclaims,
    }
  }

  action changeViews(bs: Set[int], p: int): bool = changeViewWith(bs, p, false)

  action changeView(b: int, p: int): bool = changeViews(Set(b), p)

  // The journals of bucket b which partition p holds a record of (live
  // journals, tombstones, and absent markers), except records of kind `omit`
  // (a mutant's filter).
  def heldKeys(p: int, b: int, omit: str): Set[str] =
    keysOf(b).filter(k => isPresent(jr.get(p).get(k)) and jr.get(p).get(k).kind != omit)

  // Partition p seals bucket b once a later view moves b elsewhere. With
  // `withFilter`, it writes a key filter in the same transaction: every journal
  // it holds a record of, plus false positives `fp`, plus its chain's filter
  // while its own migration is not done; if that filter is unknown (it reports
  // every key), so does this one.
  action sealWith(p: int, b: int, withFilter: bool, fp: Set[str], omit: str): bool = {
    val bs = bkt.get(p).get(b)
    val laterView = range(0, MAX_VIEWS).foldl(-1, (acc, w) =>
      if (acc >= 0 or w <= bs.ver or w >= views.length()) acc
      else if (views[w].get(b) != p) w else acc)
    all {
      bs.st == "open",
      laterView >= 0,
      bkt' = bkt.set(p, bkt.get(p).set(b, { ...bs, st: "sealed", sealVer: laterView,
        of: if (withFilter) heldKeys(p, b, omit).union(fp).union(if (bs.done) Set() else bs.cf) else KEYS })),
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, dones' = dones, reclaims' = reclaims,
    }
  }

  action seal(p: int, b: int): bool = sealWith(p, b, true, Set(), "")

  // An open of bucket b at partition m for view v is authorized once the
  // bucket's current opener has sealed it, as the coordinator records in the
  // migration's `Sealed` phase. The migration is the latest map's: m owns b
  // there, and v is the version which assigned it. The authorization carries
  // the sealed source's collection generation, which is final. It is
  // recorded in open slot i and applied later, possibly more than once
  // (duplicate requests), after the map has moved on. It also carries the key
  // filter the sealed source wrote, which is final.
  action authorizeWith(i: int, m: int, b: int, v: int, requireSeal: bool): bool = {
    val cur = currentOpener(b)
    val curState = bkt.get(cur).get(b)
    all {
      not(opens.get(i).active),
      cur >= 0,
      v == assignedAt(b, m),
      v > curState.ver,
      not(requireSeal) or curState.st == "sealed",
      opens' = opens.set(i, { active: true, m: m, b: b, v: v, src: cur, gen: curState.gen, cf: if (curState.st == "sealed") curState.of else KEYS }),
      views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, collected' = collected, dones' = dones, reclaims' = reclaims,
    }
  }

  action authorize(i: int, m: int, b: int, v: int): bool = authorizeWith(i, m, b, v, true)

  // Whether open i applies at its partition, in one transaction there. Fenced:
  // its view is later than the partition's last transition of the bucket
  // (D45). Reopen rule (D11): the partition holds no copy of the bucket, or
  // has finished reclaiming it, or is itself the source and keeps its history.
  def openable(i: int, fenced: bool): bool = {
    val t = opens.get(i)
    val mine = bkt.get(t.m).get(t.b)
    and {
      t.active,
      mine.st != "open",
      not(fenced) or t.v > localVersion(mine),
      (t.src == t.m and not(mine.reclaimed)) or mine.st == "absent" or cleared(t.m, t.b),
    }
  }

  action openTicketWith(i: int, fenced: bool): bool = {
    val t = opens.get(i)
    val mine = bkt.get(t.m).get(t.b)
    val opened =
      if (t.src == t.m) { ...mine, st: "open", ver: t.v, sealVer: -1, of: KEYS }
      else { ...NO_BUCKET, st: "open", ver: t.v, src: t.src, gen: t.gen, cf: t.cf }
    all {
      openable(i, fenced),
      bkt' = bkt.set(t.m, bkt.get(t.m).set(t.b, opened)),
      opens' = opens.set(i, NO_TICKET),
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, collected' = collected, dones' = dones, reclaims' = reclaims,
    }
  }

  action openTicket(i: int): bool = openTicketWith(i, true)

  // The partition rejects an open that does not apply, and the requester drops
  // it.
  action dropTicket(i: int): bool = all {
    opens.get(i).active,
    not(openable(i, true)),
    opens' = opens.set(i, NO_TICKET),
    views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, collected' = collected, dones' = dones, reclaims' = reclaims,
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
      views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, dones' = dones, reclaims' = reclaims,
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
      views' = views, bkt' = bkt, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, dones' = dones, reclaims' = reclaims,
    }
  }

  action deliver(i: int): bool = deliverWith(i, true, true)

  // The install procedure rejects an undeliverable pull, and the client drops
  // it (and pulls again if it still needs the journal).
  action dropPull(i: int): bool = all {
    pulls.get(i).active,
    not(deliverable(i, true, true)),
    pulls' = pulls.set(i, NO_PULL),
    views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, dones' = dones, reclaims' = reclaims,
  }

  // Whether the migration inventory of bucket b's chain from q, as the
  // consolidator enumerates it, includes journal k: the newest record of k on
  // the chain is a live journal, a tombstone, or an absent marker (D42); the
  // consolidator lists each link's live index and its marker set, which holds
  // tombstones and absent markers. `liveOnly` enumerates only live journals,
  // as the live index does.
  def inInventory(q: int, k: str, liveOnly: bool): bool = {
    val w = walk(q, k)
    or {
      not(w.finished),
      w.err,
      w.rec.kind == "live",
      not(liveOnly) and isPresent(w.rec),
    }
  }

  // Completion, first half: the consolidator enumerates the migration
  // inventory of p's bucket b from its source chain, and records it with the
  // view at which p opened the bucket (D46). The sources are sealed and are
  // not reclaimed before p is done, so the inventory is stable for that
  // opening. inventory: "full" | "live" (live journals only) | "none".
  action prepareDoneWith(i: int, p: int, b: int, inventory: str): bool = {
    val bs = bkt.get(p).get(b)
    val inv =
      if (inventory == "none") Set()
      else keysOf(b).filter(k => inInventory(bs.src, k, inventory == "live"))
    all {
      not(dones.get(i).active),
      bs.st == "open",
      not(bs.done),
      dones' = dones.set(i, { active: true, p: p, b: b, fence: bs.ver, inv: inv }),
      bkt' = bkt,
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, reclaims' = reclaims,
    }
  }

  action prepareDone(i: int, p: int, b: int): bool = prepareDoneWith(i, p, b, "full")

  // Whether completion i may still apply: its destination holds the bucket
  // open, not done, at the view it was prepared for (fenced).
  def doneApplies(i: int, fenced: bool): bool = {
    val d = dones.get(i)
    val bs = bkt.get(d.p).get(d.b)
    and { d.active, bs.st == "open", not(bs.done), not(fenced) or bs.ver == d.fence }
  }

  // Second half, possibly delayed: in one transaction at p, sets `done` once p
  // holds a record of every journal in the prepared inventory. The chain's
  // key filter is dropped: a done bucket never reads its chain.
  action completeWith(i: int, fenced: bool): bool = {
    val d = dones.get(i)
    val bs = bkt.get(d.p).get(d.b)
    all {
      doneApplies(i, fenced),
      d.inv.forall(k => isPresent(jr.get(d.p).get(k))),
      bkt' = bkt.set(d.p, bkt.get(d.p).set(d.b, { ...bs, done: true, cf: KEYS })),
      dones' = dones.set(i, NO_DONE),
      views' = views, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, reclaims' = reclaims,
    }
  }

  action completeDone(i: int): bool = completeWith(i, true)

  // p rejects a completion that no longer applies, and the consolidator drops
  // it.
  action dropDone(i: int): bool = all {
    dones.get(i).active,
    not(doneApplies(i, true)),
    dones' = dones.set(i, NO_DONE),
    views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, reclaims' = reclaims,
  }

  // Whether a read path can reach partition a's copy of bucket b: a is on the
  // migration chain of the bucket's current opener, or of a partition holding
  // it open. A copy leaves every chain once a later opener on it is done.
  def reachable(a: int, b: int): bool =
    chain(currentOpener(b), b).contains(a)
      or PARTITIONS.exists(p => bkt.get(p).get(b).st == "open" and chain(p, b).contains(a))

  // Reclamation, first half: the coordinator authorizes partition a to
  // reclaim its sealed copy of bucket b once no read path reaches it (D42),
  // bound to the version at which a sealed the bucket (D47).
  action authorizeReclaimWith(i: int, a: int, b: int, early: bool): bool = {
    val bs = bkt.get(a).get(b)
    all {
      not(reclaims.get(i).active),
      bs.st == "sealed",
      not(cleared(a, b)),
      early or not(reachable(a, b)),
      reclaims' = reclaims.set(i, { active: true, a: a, b: b, ver: localVersion(bs) }),
      views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, dones' = dones,
    }
  }

  action authorizeReclaim(i: int, a: int, b: int): bool = authorizeReclaimWith(i, a, b, false)

  // Whether reclamation i may still delete: its partition holds the bucket
  // sealed at the authorized version (fenced).
  def reclaimApplies(i: int, fenced: bool): bool = {
    val r = reclaims.get(i)
    val bs = bkt.get(r.a).get(r.b)
    and { r.active, bs.st == "sealed", not(fenced) or localVersion(bs) == r.ver }
  }

  // Second half, possibly delayed and duplicated: one deletion batch, in one
  // transaction at a, deletes the bucket's journals ks. Every batch checks the
  // fence. The first marks the bucket reclaimed, so reads there fail instead
  // of reading as absent; the bucket key stays sealed at its version. The
  // request retires once the copy is cleared.
  action reclaimBatchWith(i: int, ks: Set[str], fenced: bool): bool = {
    val r = reclaims.get(i)
    val bs = bkt.get(r.a).get(r.b)
    val jr2 = jr.get(r.a).keys().mapBy(k =>
      if (KEY_BUCKET.get(k) == r.b and ks.contains(k)) NONE else jr.get(r.a).get(k))
    all {
      reclaimApplies(i, fenced),
      bkt' = bkt.set(r.a, bkt.get(r.a).set(r.b, { ...bs, reclaimed: true })),
      jr' = jr.set(r.a, jr2),
      reclaims' =
        if (keysOf(r.b).forall(k => not(isPresent(jr2.get(k))))) reclaims.set(i, NO_RECLAIM)
        else reclaims,
      views' = views, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, dones' = dones,
    }
  }

  action reclaimBatch(i: int, ks: Set[str]): bool = reclaimBatchWith(i, ks, true)

  // a rejects a reclamation that no longer applies, and the requester drops it.
  action dropReclaim(i: int): bool = all {
    reclaims.get(i).active,
    not(reclaimApplies(i, true)),
    reclaims' = reclaims.set(i, NO_RECLAIM),
    views' = views, bkt' = bkt, jr' = jr, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, dones' = dones,
  }

  // Drops a tombstone or absent marker once migration is done (D5), whatever
  // retries or attempts are outstanding. A tombstone names the absence after
  // its deletion and carries the mutation ids that retries and attempts rely
  // on; dropping it restores the empty absence, so the copy raises its
  // collection generation past the tombstone's stamp (D25), and the empty
  // absence at the new generation becomes observable for the bucket's
  // journals. The implementation retains tombstones for longer than a retry
  // usually arrives, so retries usually find their ids; the generation keeps
  // any later retry or attempt from applying again. An absent marker carries
  // no ids.
  action dropMarkerWith(p: int, k: str, raiseGeneration: bool): bool = {
    val b = KEY_BUCKET.get(k)
    val bs = bkt.get(p).get(b)
    val r = jr.get(p).get(k)
    val deletion = r.ver - 1
    val raise = r.kind == "tomb" and raiseGeneration and r.dgen + 1 > bs.gen
    all {
      bs.st == "open",
      bs.done,
      r.kind == "absent" or r.kind == "tomb",
      jr' = jr.set(p, jr.get(p).set(k, NONE)),
      bkt' = if (raise) bkt.set(p, bkt.get(p).set(b, { ...bs, gen: r.dgen + 1 })) else bkt,
      seen' =
        if (raise) seen.keys().mapBy(j =>
          if (KEY_BUCKET.get(j) == b) seen.get(j).union(Set({ live: false, ver: 0, gen: r.dgen + 1 })) else seen.get(j))
        else seen,
      collected' =
        if (r.kind == "tomb" and deletion > collected.get(k)) collected.set(k, deletion) else collected,
      views' = views, truth' = truth, ops' = ops, outcome' = outcome, applied' = applied, log' = log, nextOp' = nextOp, pulls' = pulls, inflight' = inflight, opens' = opens, dones' = dones, reclaims' = reclaims,
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
  // move's destination has opened, so no authorized open is superseded. The
  // coordinator checks that p holds no copy of b, or has reclaimed it, which
  // it observes at p; a reclaimed copy is on no read path, and an unreclaimed
  // one must reclaim before p can reopen.
  def mayMove(b: int, p: int): bool = {
    val last = views[views.length() - 1]
    val cur = currentOpener(b)
    val mine = bkt.get(p).get(b)
    all {
      last.get(b) == cur,
      bkt.get(cur).get(b).st == "open",
      mine.st == "absent" or mine.reclaimed,
    }
  }

  // Client actions: new writes, retries of writes whose reply was lost, and
  // delayed attempts. Nondeterministic choices have distinct names, so traces
  // record each one.
  action clientStep = {
    nondet cp = PARTITIONS.oneOf()
    nondet ck = KEYS.oneOf()
    nondet cid = OPS.oneOf()
    nondet cdel = Bool.oneOf()
    nondet cobs = seen.get(ck).oneOf()
    nondet clost = Bool.oneOf()
    any {
      writeWith(cp, ck, cdel, cobs, clost, true),
      retry(cp, cid, clost),
      deliverAttempt(cp, cid),
      expire(cid),
    }
  }

  // Partition actions which carry out migrations, each the subject of a
  // fairness condition.
  action sealAny = {
    nondet sp = PARTITIONS.oneOf()
    nondet sb = BUCKETS.oneOf()
    nondet sf = Bool.oneOf()
    nondet sfp = KEYS.powerset().oneOf()
    sealWith(sp, sb, sf or not(FILTER_CHOICES), if (FILTER_CHOICES) sfp.intersect(keysOf(sb)) else Set(), "")
  }
  action authorizeAny = {
    nondet ai = OPEN_SLOTS.oneOf()
    nondet ap = PARTITIONS.oneOf()
    nondet ab = BUCKETS.oneOf()
    nondet av = 0.to(MAX_VIEWS - 1).oneOf()
    authorize(ai, ap, ab, av)
  }
  action openAny = { nondet oi = OPEN_SLOTS.oneOf() openTicket(oi) }
  action dropTicketAny = { nondet ti = OPEN_SLOTS.oneOf() dropTicket(ti) }
  action fetchAny = {
    nondet fi = PULL_SLOTS.oneOf()
    nondet fp = PARTITIONS.oneOf()
    nondet fk = KEYS.oneOf()
    fetch(fi, fp, fk)
  }
  action deliverAny = { nondet li = PULL_SLOTS.oneOf() deliver(li) }
  action dropPullAny = { nondet xi = PULL_SLOTS.oneOf() dropPull(xi) }
  action prepareDoneAny = {
    nondet di = DONE_SLOTS.oneOf()
    nondet dp = PARTITIONS.oneOf()
    nondet db = BUCKETS.oneOf()
    prepareDone(di, dp, db)
  }
  action completeAny = { nondet ci = DONE_SLOTS.oneOf() completeDone(ci) }
  action dropDoneAny = { nondet ei = DONE_SLOTS.oneOf() dropDone(ei) }
  action authorizeReclaimAny = {
    nondet ri = RECLAIM_SLOTS.oneOf()
    nondet rp = PARTITIONS.oneOf()
    nondet rb = BUCKETS.oneOf()
    authorizeReclaim(ri, rp, rb)
  }
  action reclaimBatchAny = {
    nondet bi = RECLAIM_SLOTS.oneOf()
    nondet bk = KEYS.oneOf()
    reclaimBatch(bi, Set(bk))
  }
  action dropReclaimAny = { nondet gi = RECLAIM_SLOTS.oneOf() dropReclaim(gi) }
  action dropMarkerAny = { nondet mp = PARTITIONS.oneOf() nondet mk = KEYS.oneOf() dropMarker(mp, mk) }

  action protocolStep = any {
    sealAny, authorizeAny, openAny, dropTicketAny, fetchAny, deliverAny, dropPullAny,
    prepareDoneAny, completeAny, dropDoneAny, authorizeReclaimAny, reclaimBatchAny, dropReclaimAny, dropMarkerAny,
  }

  // Views change by the coordinator's rule.
  action step = {
    nondet vbs = BUCKETS.powerset().exclude(Set(Set())).oneOf()
    nondet vp = PARTITIONS.oneOf()
    any { clientStep, changeViewWith(vbs, vp, true), protocolStep }
  }

  // As `step`, with views changed freely.
  action uncoordinatedStep = {
    nondet vbs = BUCKETS.powerset().exclude(Set(Set())).oneOf()
    nondet vp = PARTITIONS.oneOf()
    any { clientStep, changeViews(vbs, vp), protocolStep }
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
  // mutations of its journal have committed and no tombstone of a deletion at
  // or after it has been dropped; only then may a committed op report an
  // unknown outcome.
  val retryOutcome = OPS.forall(id =>
    (outcome.get(id) == "ambiguous" and applied.get(id) == 1)
      implies (laterMutations(id) >= RECENT or collected.get(ops.get(id).key) >= id))

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
      PARTITIONS.forall(q => q == o or bkt.get(q).get(b).st == "absent" or cleared(q, b)),
    }
  }

  val allComplete = BUCKETS.forall(b => complete(b))

  val vars = (views, bkt, jr, truth, ops, outcome, applied, log, nextOp, pulls, seen, inflight, opens, dones, reclaims, collected)

  temporal fairness = and {
    weakFair(sealAny, vars),
    weakFair(authorizeAny, vars),
    weakFair(openAny, vars),
    weakFair(dropTicketAny, vars),
    weakFair(fetchAny, vars),
    weakFair(deliverAny, vars),
    weakFair(dropPullAny, vars),
    weakFair(prepareDoneAny, vars),
    weakFair(completeAny, vars),
    weakFair(dropDoneAny, vars),
    weakFair(authorizeReclaimAny, vars),
    weakFair(reclaimBatchAny, vars),
    weakFair(dropReclaimAny, vars),
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
    nondet i = DONE_SLOTS.oneOf()
    any { step, prepareDoneWith(i, p, b, "live") }
  }

  // Drops a tombstone without raising the copy's collection generation, so a
  // retry or delayed attempt of a creation which observed the absence before
  // the deletion applies again.
  action mutantDropWithoutHorizon = {
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    any { step, dropMarkerWith(p, k, false) }
  }

  // Orders retries against collected tombstones by client clocks, which may be
  // skewed: a write carries its issue time, a deletion stamps its tombstone
  // with its own client's time, and a retry issued at or before a dropped
  // deletion's time is rejected. A creator whose clock runs ahead of the
  // deleter's passes the check, and its retry applies again.
  action mutantClientClock = {
    nondet p = PARTITIONS.oneOf()
    nondet k = KEYS.oneOf()
    nondet del = Bool.oneOf()
    nondet lost = Bool.oneOf()
    nondet skew = Set(-1, 0, 1).oneOf()
    any { step, writeWithClock(p, k, del, current(p, k), lost, true, if (nextOp + skew < 0) 0 else nextOp + skew) }
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
      ops' = ops.set(id, { key: k, del: true, live: true, expect: r.ver, obs: bs.gen }),
      nextOp' = nextOp + 1,
      jr' = jr.set(p, jr.get(p).set(k, NONE)),
      truth' = truth.set(k, List()),
      applied' = applied.set(id, 1),
      log' = log.set(k, log.get(k).append(id)),
      outcome' = outcome.set(id, "acked"),
      views' = views, bkt' = bkt, pulls' = pulls, seen' = seen, inflight' = inflight, opens' = opens, collected' = collected, dones' = dones, reclaims' = reclaims,
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
    nondet i = RECLAIM_SLOTS.oneOf()
    any { step, authorizeReclaimWith(i, p, b, true) }
  }

  // Marks migration done before every journal is consolidated.
  action mutantMarkDoneEarly = {
    nondet p = PARTITIONS.oneOf()
    nondet b = BUCKETS.oneOf()
    nondet i = DONE_SLOTS.oneOf()
    any { step, prepareDoneWith(i, p, b, "none") }
  }

  // Applies a completion without the destination's opening fence, so a
  // delayed completion prepared for an earlier opening marks a reopened
  // bucket done against a stale inventory.
  action mutantUnfencedDone = {
    nondet i = DONE_SLOTS.oneOf()
    any { step, completeWith(i, false) }
  }

  // Deletes without the source's sealed-version fence, so a delayed
  // duplicate reclamation deletes a later tenure's records.
  action mutantUnfencedReclaim = {
    nondet i = RECLAIM_SLOTS.oneOf()
    nondet k = KEYS.oneOf()
    any { step, reclaimBatchWith(i, Set(k), false) }
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

  // Builds the key filter from live journals and absent markers only,
  // omitting tombstones: the destination answers for a deleted journal as the
  // empty absence, without the deletion's identity and history, so a retried
  // creation applies again.
  action mutantFilterOmitsTombstone = {
    nondet p = PARTITIONS.oneOf()
    nondet b = BUCKETS.oneOf()
    any { step, sealWith(p, b, true, Set(), "tomb") }
  }

  // Builds the key filter without the live journals, so the destination
  // answers absent for a journal the source holds.
  action mutantFilterOmitsJournal = {
    nondet p = PARTITIONS.oneOf()
    nondet b = BUCKETS.oneOf()
    any { step, sealWith(p, b, true, Set(), "live") }
  }
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
    MAX_DONES = 1,
    MAX_RECLAIMS = 1,
    RECENT = 1,
    FILTER_CHOICES = false
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
    MAX_DONES = 1,
    MAX_RECLAIMS = 1,
    RECENT = 1,
    FILTER_CHOICES = false
  ).*
}
