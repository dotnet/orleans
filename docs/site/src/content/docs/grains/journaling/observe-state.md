---
title: Observe journaled state and stream drafts
description: Watch Orleans Journaling values, read an append-only durable log, and stream volatile drafts to readers.
ms.date: 11/20/2026
ms.topic: how-to
---

# Observe journaled state and stream drafts

Watch streams deliver the committed changes made to a grain's journaled state. A grain exposes them to callers as <xref:System.Collections.Generic.IAsyncEnumerable`1> methods. <xref:Orleans.Journaling.IDurableLog`1> adds an append-only log whose readers resume from a sequence number, and drafts which stream volatile content, such as model output, before its durable entry exists.

Configure journaling as described in [Use durable state](durable-state.md).

## Expose a watch from a grain

This grain records a conversation in a durable log and streams each response through a draft:

:::code language="csharp" source="../../snippets/compiled-durable/Grains/JournalingObservationSnippets.cs" id="conversation_grain_interface":::

:::code language="csharp" source="../../snippets/compiled-durable/Grains/JournalingObservationSnippets.cs" id="conversation_grain":::

Orleans enumerates a grain's `IAsyncEnumerable<T>` result through interleaving calls, so watches keep delivering while `Ask` streams a response. Enumerate a watch in the owning activation's execution context, as grain methods do. `End` seals the log, which ends every watch of it.

## Committed delivery

Watch streams deliver only durable state. Each change is tagged with an activation-local write version: the version of the journal write which captures it. Each write captures every state, persists what it captured, and then publishes one watermark for its version to every watcher, which delivers the changes that write made durable. A change staged by another turn after the write captured its entries waits for the following write, so readers never observe it early. The same rule governs other acknowledgements: a durable task completion source's task completes after the write which captured its result.

Durable state takes ownership of the values, items, and entries passed to it, and the journal records each change when it is made. To change one, assign or append a new instance. Watch streams deliver those same instances, so an observed change matches what its write stored.

## Watch a value

<xref:Orleans.Journaling.DurableStateWatchExtensions.WatchAsync*> watches an <xref:Orleans.Journaling.IDurableValue`1>. The stream begins with the current value once it is durable, so a watch which begins while the value is staged receives it after the next write, and then delivers the latest value after each write which changes it. Each <xref:Orleans.Journaling.ValueChange`1> carries the value and the write version which made it durable. A slow reader receives the newest value and skips intermediate values, so a value watch retains no backlog.

A value which nobody watches publishes nothing and allocates nothing for observation. The manager creates its observer state when the first stream begins, once it has recovered its state.

## Read a durable log

<xref:Orleans.Journaling.IDurableLog`1>, created with <xref:Orleans.Journaling.DurableStateManagerExtensions.GetOrAddLog*>, assigns each appended entry the next value of a durable, monotonically increasing sequence number:

- <xref:Orleans.Journaling.IDurableLog`1.Append*> stages an entry and returns its sequence number.
- <xref:Orleans.Journaling.IDurableLog`1.FirstSequence>, <xref:Orleans.Journaling.IDurableLog`1.NextSequence>, and <xref:Orleans.Journaling.IDurableLog`1.CommittedSequence> describe the retained, appended, and durable entries. Serve paged history from entries before `CommittedSequence`.
- <xref:Orleans.Journaling.IDurableLog`1.TrimBefore*> discards old entries to bound retention. The retained entries remain valid.
- <xref:Orleans.Journaling.IDurableLog`1.Clear*> discards every entry to replace them, for example with a compacted summary. A clear consumes one sequence number, so sequence numbers are never reused within a history.
- <xref:Orleans.Journaling.IDurableLog`1.Epoch> identifies the history. The first change after the log is created, or after the grain's journal is deleted, begins a history with a new random epoch, which is journaled with it. Deleting the journal restarts sequence numbers at zero in the new history.
- <xref:Orleans.Journaling.IDurableLog`1.Seal*> durably ends the log. A sealed log rejects `Append`, `Clear`, and `BeginDraft`, abandons open drafts, and can still be trimmed. Only deleting the journal unseals it.

<xref:Orleans.Journaling.IDurableLog`1.ReadAsync*> reads retained entries from a position and then waits for new ones. Readers re-read entries from the log at their position instead of buffering them, so a slow reader retains no entries. A position is an epoch and a sequence number. Every event carries the epoch of its history in <xref:Orleans.Journaling.LogEvent`1.Epoch>, so an epoch and a sequence number form a complete replay checkpoint. Pass zero as the epoch to read the current history. Besides entries, a stream delivers these <xref:Orleans.Journaling.LogEventKind> events:

| Event | Meaning |
| --- | --- |
| `Trimmed` | `TrimBefore` discarded the entries before `Sequence`. Every active reader receives it, including readers which have read every entry. A reader positioned before `Sequence` continues from it. |
| `Reset` | The entries the reader observed were replaced: `Clear` discarded them, the journal was deleted, the reader resumed with another history's epoch, or the reader's position follows the end of the log. Discard the entries read before it. The stream continues from `Sequence` in the history identified by `Epoch`. |
| `CaughtUp` | The stream has delivered every entry which was committed when the read began. It's delivered once. |
| `Sealed` | The log ended before `Sequence`. It's the stream's final event. |

A stream which begins before the first retained entry starts with `Trimmed`. A stream which begins with another history's epoch, or at or before a cleared sequence number, starts with `Reset`, including after the grain reactivates, while a stream which begins after the clear doesn't. Streams receive entries, `Trimmed`, `Reset`, and `Sealed` once the append, trim, clear, or seal is durable, so every entry a reader receives is a valid resume point. A reader positioned in entries which an uncommitted trim has discarded waits for the trim's write. To resume, pass the epoch of the latest event and the position after the latest entry received.

`Sealed` distinguishes a log which ended from an activation which stopped. When the activation stops, streams end without it.

## Stream drafts

<xref:Orleans.Journaling.IDurableLog`1.BeginDraft*> opens an <xref:Orleans.Journaling.IDurableLogDraft`1>:

- `Write` delivers a volatile item immediately to streams which include drafts: pass `includeDrafts: true` to `ReadAsync`. The draft holds its items in memory only while it is open.
- `Commit` closes the draft and appends zero or more entries which replace its items. The entries form one journal entry, so one write makes all of them durable, and an entry which can't be encoded leaves the draft open without appending any entry. Readers receive `DraftCommitted`, whose `Sequence` and `Count` give the range of committed entries, followed by the entries. `Commit` with no entries closes the draft cleanly.
- `Abandon` closes the draft without entries and reports `DraftAbandoned` with the owner's `Message`. Disposing an open draft abandons it, so a failed operation, such as a model stream which fails mid-response, releases its draft.

A stream reports `DraftCommitted` once the committed entries are durable. A stream which includes drafts and begins while a draft is open receives the draft's items from position zero. When a draft closes, its items are released: a reader which hasn't yet read every item receives `DraftCommitted` or `DraftAbandoned` next and never receives the remaining items. The committed entries carry the final content.

`DraftAbandoned` carries a <xref:Orleans.Journaling.LogDraftAbandonReason>:

| Reason | Cause |
| --- | --- |
| `Abandoned` | The owner called `Abandon`. |
| `Disposed` | The owner disposed the open draft. |
| `JournalDeleted` | The log's journal was deleted. |
| `Sealed` | The log was sealed while the draft was open. |
| `PreviousActivation` | Another activation opened the draft and the log retains no commit of it, so its content was lost. |
| `Unknown` | This activation neither retains a commit of the draft nor records why it closed. |

The log closes a draft when its journal is deleted or the log is sealed. Its owner then holds a closed draft, whose `Write` and `Commit` throw an <xref:System.InvalidOperationException> that states why it closed. Check `IsOpen` before committing a draft which the log can close.

Each <xref:Orleans.Journaling.LogDraftId> includes an identifier for the log instance, and therefore the activation, which opened the draft. A reconnecting reader passes its position and the draft it was viewing. If that draft is still open, the stream repeats its items from position zero. The log journals a draft's identifier with its committed entries, so when the draft committed, the stream reports `DraftCommitted` with the range which replaced it, in any activation, while the log retains the first of those entries. If this activation abandoned the draft, the stream reports `DraftAbandoned` with its reason; each log records the reasons for its 16 most recently abandoned drafts and reports `Unknown` for older ones. A draft which a previous activation opened and never committed is reported as `PreviousActivation`; regenerate its content from the last committed entry.

This client renders committed history and in-progress drafts and tracks its resume position:

:::code language="csharp" source="../../snippets/compiled-durable/Grains/JournalingObservationSnippets.cs" id="conversation_client":::

## Failure and shutdown

A failed journal write fences the manager and requests grain deactivation. Watch streams then fault with an <xref:System.InvalidOperationException> whose inner exception is the storage failure, and readers never receive the changes the failed write contained. Readers reconnect to the new activation, which recovers the durable state. When the manager stops normally, streams complete. Cancelling a stream's token ends that stream.

Deleting the grain's journal delivers the default value to value watches, or `Reset` for a log reader which observed entries, and abandons open drafts. A reader following a draft whose committed entries the deletion discarded receives `DraftAbandoned` with `JournalDeleted`.

## Test code which uses a log

Test grains which use durable state against real journaled state managers. An <xref:Orleans.TestingHost.InProcessTestCluster> configured with volatile journal storage keeps journals in silo memory, and <xref:Orleans.TestingHost.InProcessTestCluster.DeactivateAsync*> exercises recovery in a new activation. Configure the journal format as production silos do, or, as this test does, with reflection-based JSON metadata:

:::code language="csharp" source="../../snippets/compiled-durable/Grains/JournalingObservationSnippets.cs" id="journaling_test_cluster":::

Host code which takes an `IDurableLog<T>` in a test grain to test it the same way. A hand-written `IDurableLog<T>` substitute doesn't reproduce durability, drafts, or read streams.
