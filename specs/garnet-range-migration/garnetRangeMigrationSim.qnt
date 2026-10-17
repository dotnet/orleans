// Configuration for random simulation and directed scenarios. It lives in its
// own file so that it adds nothing to the IR which TLC's translation of
// garnetRangeMigration.qnt sends to Apalache.
module garnetRangeMigrationSim {
  import garnetRangeMigration(
    PARTITIONS = Set(1, 2, 3),
    KEYS = Set("a", "b", "c"),
    KEY_BUCKET = Map("a" -> 0, "b" -> 0, "c" -> 1),
    MAX_VIEWS = 5,
    MAX_OPS = 5,
    MAX_PULLS = 2,
    MAX_OPENS = 2,
    MAX_DONES = 2,
    MAX_RECLAIMS = 2,
    RECENT = 2,
    FILTER_CHOICES = true
  ).* from "./garnetRangeMigration"

  // Completes bucket b's migration at p, and reclaims a's copy of bucket b,
  // each through slot 0 without delay.
  run markDone(p: int, b: int): bool = prepareDone(0, p, b).then(completeDone(0))

  run reclaim(a: int, b: int): bool = authorizeReclaim(0, a, b).then(reclaimBatch(0, keysOf(b)))

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
      .then(authorizeReclaim(0, 1, 0))
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

  // A move back to an earlier owner waits until it has reclaimed its copy.
  run coordinatorRuleAllowsReturnAfterReclaimTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(markDone(2, 0))
      .then(reclaim(1, 0))
      .then(changeViewWith(Set(0), 1, true))
      .then(seal(2, 0))
      .then(authorize(0, 1, 0, 2)).then(openTicket(0))
      .expect(safety and openAt(0) == Set(1))

  run coordinatorRuleWaitsForReclaimTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(markDone(2, 0))
      .then(changeViewWith(Set(0), 1, true))
      .fail()

  // Consolidation needs only the journals the chain holds: "b", held by no
  // partition, needs no absent marker.
  run markDoneNeedsOnlyChainRecordsTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(markDone(2, 0))
      .expect(safety and not(isPresent(jr.get(2).get("b"))))

  // A partition opens a bucket only at the view which moved it there.
  run openAtMovingViewTest =
    init
      .then(changeView(0, 2))
      .then(changeView(1, 1))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 2))
      .fail()

  // A creation's reply is lost; another client deletes the journal, and the
  // tombstone is collected before the creation's retry arrives.
  run collectedTombstone =
    init
      .then(write(1, "a", false, true))
      .then(write(1, "a", true, false))
      .then(dropMarker(1, "a"))

  // The retry observed the absence before the deletion, so the copy's
  // collection generation rejects it: the outcome is unknown, and the
  // creation applied once.
  run collectedTombstoneRetryIsRejectedTest =
    collectedTombstone
      .then(retry(1, 0, false))
      .expect(safety and outcome.get(0) == "ambiguous" and applied.get(0) == 1 and truth.get("a") == List())

  // An op which observes the absence after the collection is unaffected.
  run collectedTombstoneAllowsNewCreateTest =
    collectedTombstone
      .then(write(1, "a", false, false))
      .expect(safety and truth.get("a") == List(2))

  // The generation travels with the bucket to the next opener, where the
  // pulled journal is an absent marker.
  run horizonTravelsWithBucketTest =
    collectedTombstone
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(retry(2, 0, false))
      .expect(safety and bkt.get(2).get(0).gen == 1 and outcome.get(0) == "ambiguous")

  // Every journal of bucket 0 is in 2, but 2's migration is not done: 1's copy
  // stays on 2's chain, so 1 neither reclaims nor takes the bucket back.
  run reclaimWaitsForDoneTest =
    moveBucket0
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(fetch(0, 2, "b")).then(deliver(0))
      .then(authorizeReclaim(0, 1, 0))
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

  // Collection does not wait for the queued attempt: dropping the tombstone
  // raises the collection generation past the creation's observation, so the
  // attempt, delivered later, is rejected.
  run collectWithAttemptRejectedTest =
    queuedAttemptSetup
      .then(dropMarker(1, "a"))
      .then(deliverAttempt(1, 0))
      .expect(safety and applied.get(0) == 1 and truth.get("a") == List() and bkt.get(1).get(0).gen == 1)

  run collectAfterExpiryTest =
    queuedAttemptSetup
      .then(expire(0))
      .then(dropMarker(1, "a"))
      .expect(safety and jr.get(1).get("a").kind == "none")

  // A client observed no journal; another writer then created and deleted it,
  // and its tombstone was collected. The client's creation names the
  // generation it observed, which the collection has passed, so it conflicts;
  // the implementation then observes the absence again, and a creation
  // conditioned on the new observation applies.
  run staleAbsenceAfterCollectionTest =
    init
      .then(write(1, "a", false, false))
      .then(write(1, "a", true, false))
      .then(dropMarker(1, "a"))
      .then(writeWith(1, "a", false, { live: false, ver: 0, gen: 0 }, false, true))
      .then(writeWith(1, "a", false, { live: false, ver: 0, gen: 1 }, false, true))
      .expect(safety and outcome.get(2) == "conflict" and outcome.get(3) == "acked" and truth.get("a") == List(3))

  // A condition on a live journal or a tombstone never matches again.
  run staleLiveConditionRejectedTest =
    init
      .then(write(1, "a", false, false))
      .then(write(1, "a", true, false))
      .then(writeWith(1, "a", false, { live: true, ver: 1, gen: 0 }, false, true))
      .expect(safety and outcome.get(2) == "conflict" and truth.get("a") == List())

  // A completion of 2's first opening of bucket 0 (slot 1), with an empty
  // inventory, is delayed while the bucket moves 1 -> 2 -> 1 -> 2, gains a
  // write in 1, and 2 reclaims and reopens it, empty, from 1.
  run delayedDoneSetup =
    init
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))
      .then(prepareDone(1, 2, 0))
      .then(markDone(2, 0))
      .then(reclaim(1, 0))
      .then(changeView(0, 1))
      .then(seal(2, 0))
      .then(authorize(0, 1, 0, 2)).then(openTicket(0))
      .then(fetch(0, 1, "a")).then(deliver(0))
      .then(write(1, "a", false, false))
      .then(markDone(1, 0))
      .then(reclaim(2, 0))
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 3)).then(openTicket(0))

  // The completion is fenced on the opening it was prepared for, so it is
  // rejected and dropped, and 2 pulls the write.
  run delayedDoneFencedTest =
    delayedDoneSetup
      .then(dropDone(1))
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(markDone(2, 0))
      .expect(safety and jr.get(2).get("a").vals == List(0))

  run delayedDoneRejectedTest =
    delayedDoneSetup
      .then(completeDone(1))
      .fail()

  // A duplicate reclamation of 1's copy of bucket 0 (slot 1), authorized for
  // 1's first tenure, is delayed while 1 reclaims, takes the bucket back,
  // commits a write, and hands the bucket to 3, which has not pulled it.
  run delayedReclaimSetup =
    init
      .then(write(1, "a", false, false))
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(fetch(0, 2, "b")).then(deliver(0))
      .then(markDone(2, 0))
      .then(authorizeReclaim(1, 1, 0))
      .then(reclaim(1, 0))
      .then(changeView(0, 1))
      .then(seal(2, 0))
      .then(authorize(0, 1, 0, 2)).then(openTicket(0))
      .then(fetch(0, 1, "a")).then(deliver(0))
      .then(write(1, "a", false, false))
      .then(changeView(0, 3))
      .then(seal(1, 0))
      .then(authorize(0, 3, 0, 3)).then(openTicket(0))

  // Each deletion batch checks the version 1 sealed the bucket at, so the
  // duplicate is rejected, and 3 pulls the write from 1.
  run delayedReclaimFencedTest =
    delayedReclaimSetup
      .then(dropReclaim(1))
      .then(fetch(0, 3, "a")).then(deliver(0))
      .expect(safety and jr.get(3).get("a").vals == List(0, 1))

  run delayedReclaimRejectedTest =
    delayedReclaimSetup
      .then(reclaimBatch(1, Set("a")))
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
      .then(write(2, "a", false, false))
      .then(deliverWith(0, false, true))
      .expect(not(safety))

  run reclaimBeforeDoneTest =
    moveBucket0
      .then(authorizeReclaimWith(0, 1, 0, true)).then(reclaimBatch(0, keysOf(0)))
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

  // Without the fence, the delayed completion marks 2 done against the empty
  // inventory of its first opening, and the write in 1 is lost.
  run unfencedDoneHidesWriteTest =
    delayedDoneSetup
      .then(completeWith(1, false))
      .expect(not(safety))

  // Without the fence, the delayed duplicate deletes 1's newer record, which
  // 3 has not pulled.
  run unfencedReclaimLosesWriteTest =
    delayedReclaimSetup
      .then(reclaimBatchWith(1, Set("a"), false))
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
      .then(prepareDoneWith(0, 2, 0, "live")).then(completeDone(0))
      .then(retry(2, 0, false))
      .expect(not(safety))

  // Collecting the tombstone without raising the generation while the
  // creation's first attempt is queued lets that attempt apply again.
  run collectWithAttemptResurrectsTest =
    queuedAttemptSetup
      .then(dropMarkerWith(1, "a", false))
      .then(deliverAttempt(1, 0))
      .expect(not(safety))

  run dropWithoutHorizonAppliesTwiceTest =
    init
      .then(write(1, "a", false, true))
      .then(write(1, "a", true, false))
      .then(dropMarkerWith(1, "a", false))
      .then(retry(1, 0, false))
      .expect(not(safety))

  // The creator's clock runs ahead: its lost creation (op 0) is stamped 1, the
  // deletion (op 1) 0. Dropping the tombstone admits client times after 0, so
  // the retry applies again.
  run clientClockSkewAppliesTwiceTest =
    init
      .then(writeWithClock(1, "a", false, current(1, "a"), true, true, 1))
      .then(writeWithClock(1, "a", true, current(1, "a"), false, true, 0))
      .then(dropMarker(1, "a"))
      .then(retry(1, 0, false))
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

  // Key filters (D36). The source's filter lists "a", so the destination
  // answers for "b", which no partition on the chain holds, without a pull.
  run filterAnswersAbsentTest =
    moveBucket0
      .then(write(2, "b", false, false))
      .expect(safety and truth.get("b") == List(1) and jr.get(2).get("b").kind == "live")

  // A journal the filter reports needs a pull first.
  run filterReportsHeldJournalTest =
    moveBucket0
      .then(write(2, "a", false, false))
      .fail()

  // A false positive costs a pull, never correctness.
  run filterFalsePositivePullsTest =
    init
      .then(write(1, "a", false, false))
      .then(changeView(0, 2))
      .then(sealWith(1, 0, true, Set("b"), ""))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))
      .then(write(2, "b", false, false))
      .fail()

  // Without a filter, every journal the destination holds no record of needs a pull.
  run noFilterPullsTest =
    init
      .then(write(1, "a", false, false))
      .then(changeView(0, 2))
      .then(sealWith(1, 0, false, Set(), ""))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))
      .then(write(2, "b", false, false))
      .fail()

  // A source whose own migration is incomplete passes its chain's filter on:
  // 3's filter covers 2's records and 1's.
  run chainedFilterTest =
    moveBucket0
      .then(changeView(0, 3))
      .then(seal(2, 0))
      .then(authorize(0, 3, 0, 2)).then(openTicket(0))
      .then(write(3, "b", false, false))
      .expect(safety and bkt.get(3).get(0).cf == Set("a"))

  run chainedFilterReportsHeldJournalTest =
    moveBucket0
      .then(changeView(0, 3))
      .then(seal(2, 0))
      .then(authorize(0, 3, 0, 2)).then(openTicket(0))
      .then(write(3, "a", false, false))
      .fail()

  // A source opened without a filter whose migration is incomplete cannot
  // describe its chain, so it writes no filter.
  run unknownChainWritesNoFilterTest =
    init
      .then(write(1, "a", false, false))
      .then(changeView(0, 2))
      .then(sealWith(1, 0, false, Set(), ""))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))
      .then(changeView(0, 3))
      .then(seal(2, 0))
      .then(authorize(0, 3, 0, 2)).then(openTicket(0))
      .then(write(3, "b", false, false))
      .fail()

  // A journal answered from the filter reads as the empty absence at the
  // collection generation the bucket carried from its source, as an absent
  // marker would: a retried creation observed before a collected deletion is
  // still rejected.
  run filterKeepsCollectionGenerationTest =
    collectedTombstone
      .then(changeView(0, 2))
      .then(seal(1, 0))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))
      .then(retry(2, 0, false))
      .expect(safety and bkt.get(2).get(0).gen == 1 and not(isPresent(jr.get(2).get("a"))) and outcome.get(0) == "ambiguous")

  // The filter includes the tombstone, so the destination pulls it, and the
  // retried creation finds its id there.
  run filterReportsTombstoneTest =
    lostCreateDeleteSetup
      .then(retry(2, 0, false))
      .fail()

  run filterWithTombstonePullRecognizesRetryTest =
    lostCreateDeleteSetup
      .then(fetch(0, 2, "a")).then(deliver(0))
      .then(retry(2, 0, false))
      .expect(safety and outcome.get(0) == "acked" and applied.get(0) == 1)

  // A filter which omits a held journal makes the destination read it as absent.
  run filterOmitsJournalLosesWritesTest =
    init
      .then(write(1, "a", false, false))
      .then(changeView(0, 2))
      .then(sealWith(1, 0, true, Set(), "live"))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))
      .expect(not(safety))

  // A filter which omits a tombstone loses the deletion's identity and
  // history: the retried creation applies a second time.
  run filterOmitsTombstoneAppliesTwiceTest =
    init
      .then(write(1, "a", false, true))
      .then(write(1, "a", true, false))
      .then(changeView(0, 2))
      .then(sealWith(1, 0, true, Set(), "tomb"))
      .then(authorize(0, 2, 0, 1)).then(openTicket(0))
      .then(retry(2, 0, false))
      .expect(not(safety))
}
