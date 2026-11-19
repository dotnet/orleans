# Live application durability scenarios

These tests run ordinary `InProcessTestCluster` silos. They compile the existing
catalog application and external-effect recipe into the test assembly, using
production durable tasks, journals, inboxes, outboxes, and job handlers.

Each class runs with both JSON and Orleans binary journal formats. Each test
owns its cluster, committed byte store, controls, and clocks.

## Contracts and oracles

| Test method | Contract and deciding evidence |
|---|---|
| `CommittedReservationIntentRecoversBusinessResultAfterSenderDeactivation` | A committed outgoing reservation survives graceful sender deactivation. The recovered order succeeds, stock moves by exactly the requested quantity, and the retry retains its message identity and send time. |
| `LostAcceptanceReplyRedeliversSameReservationAfterReceiverRecovery` | The receiver commits acceptance before its reply is lost. After receiver recovery, redelivery returns `Duplicate` and stock is reserved once. Sender completion is allowed before acceptance retry: its separate completion message can already establish the result. |
| `CommittedReservationIntentRecoversBusinessResultAfterImmediateSiloLoss` | The outgoing reservation survives the testing host's immediate silo loss and sender recovery on a different silo, without client resubmission. |
| `CancellationAfterOneParallelReservationCompensatesOnlyTheSuccessfulLine` | Cancellation releases the completed line, not the parallel line still awaiting validation. The first release returns true, the second release task does not exist, and both stock balances return to their initial values. |
| `EffectIntentAcknowledgementFollowsStorageNotStaging` | Before append admission, independent recovery finds only the existing intent, not the new one. After storage acknowledgement but before the continuation, recovery finds the new intent although submission has not answered. |
| `PaymentAppliedWithLostResponseRetriesTheSameKeyAfterOwnerRecovery` | The provider applies one payment. Owner recovery and retry reuse the provider key, use distinct attempt IDs, and produce the authorized business outcome. An explicit not-yet-due invocation does not dispatch. |
| `InterruptedNonRepeatableEmailRecoversItsReconciliationOwnerWithoutRedispatch` | The provider commits email application, then the owner's result write fails before storage. Recovery retains the committed attempt marker and responsible operator. An unknown lookup retains reconciliation ownership and a future action instead of redispatching. |
| `ExhaustedRetryDecisionAndAuthorizedFollowUpSurviveImmediateSiloLoss` | Two proven-no-effect attempts exhaust the budget. The terminal failure and separately authorized follow-up retain their decision, distinct identities, and linkage across immediate silo loss. |

Business expectations are independent of recovery implementation: stock
arithmetic, one provider-applied entry, stable logical keys, distinct attempts,
and explicit terminal or reconciliation states. Read-only projections replay
committed bytes using production codecs into fresh objects. They do not read
the live activation's state or retain references to its values.

## Barriers and clocks

- `OutboxRetryScheduled` describes **staging**, not commitment. A registered
  state-machine witness captures the staged retry and signals from
  `OnWriteCompleted` only after that capture's storage acknowledgement.
- The delivery filter fails before receiver admission, or lets the receiver
  return `Accepted` and then loses that answer. Its explicit second-answer
  barrier observes redelivery independently of the order's completion.
- `ScenarioJournalGate` holds one targeted write before storage admission or
  after acknowledgement and before the caller's continuation. Provider
  application is inspected from its independently journaled bytes while the
  provider's answer is held.
- Reservation retries advance the existing fixture's **Durable Jobs clock**.
  External-effect scenarios advance only the recipe's **keyed owner clock**
  and invoke its production `RunDueAsync` handler explicitly. Their automatic
  job delivery remains in the future; they do not claim scheduler-delivery
  coverage for the external-effect recipe.
- Clocks follow real time plus controlled offsets. Hour-long backoff boundaries
  keep incidental elapsed milliseconds irrelevant. No sleep or quiet interval
  establishes completion. Bounded wall-clock waits diagnose stalls with phase,
  entity, task status, active silo, clocks, and the last observed barrier.

## Failure-model limits

Graceful activation deactivation, immediate silo loss, failed journal-provider
writes, and lost external responses are separate scenarios. The shared volatile
provider retains copied serialized bytes across activation and silo loss.
It is not a persistent device: these tests do not establish OS-process-kill,
power-loss, fsync, or filesystem-reordering guarantees. A failed
journal-provider write exercises journal fencing and recovery, not a local-disk
fatal-error handler.

These are live application scenarios, not compatibility replay tests.
Historical upgrade compatibility remains the responsibility of the existing
history-replay suites.
