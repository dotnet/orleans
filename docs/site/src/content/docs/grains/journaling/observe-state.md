---
title: Observe journaled state and stream drafts
description: Watch Orleans Journaling values and collections, read an append-only durable log, and stream volatile drafts to readers.
ms.date: 10/14/2026
ms.topic: how-to
---

# Observe journaled state and stream drafts

Watch streams deliver the changes made to a grain's journaled state. A grain exposes them to callers as <xref:System.Collections.Generic.IAsyncEnumerable`1> methods. <xref:Orleans.Journaling.IDurableLog`1> adds an append-only log whose readers resume from a sequence number, and drafts which stream volatile content, such as model output, before its durable entry exists.

All Journaling APIs are experimental and carry diagnostic `ORLEANSEXP005`. Configure journaling as described in [Use durable state](durable-state.md).

## Expose a watch from a grain

This grain records a conversation in a durable log and streams each response through a draft:

:::code language="csharp" source="../../snippets/compiled/Grains/JournalingObservationSnippets.cs" id="conversation_grain_interface":::

:::code language="csharp" source="../../snippets/compiled/Grains/JournalingObservationSnippets.cs" id="conversation_grain":::

Orleans enumerates a grain's `IAsyncEnumerable<T>` result through interleaving calls, so watches keep delivering while `Ask` streams a response. Enumerate a watch in the owning activation's execution context, as grain methods do.

## Choose a watch mode

<xref:Orleans.Journaling.DurableWatchMode> selects what a stream delivers:

- <xref:Orleans.Journaling.DurableWatchMode.Committed> delivers a change after the storage provider acknowledges the journal write which contains it. Readers observe only durable state. This mode is the default.
- <xref:Orleans.Journaling.DurableWatchMode.Speculative> delivers draft and staged changes as soon as they are made, then reports their durability.

Every event carries a <xref:Orleans.Journaling.DurableChangeStatus>:

| Status | Meaning |
| --- | --- |
| `Draft` | Volatile draft content held in the current activation's memory. The journal never contains it. |
| `Staged` | Applied in memory. The next acknowledged journal write makes it durable. |
| `Committed` | Durable in journal storage. |

Each change is tagged with an activation-local write version: the version of the journal write which captures it. When a write is acknowledged, the manager publishes one watermark for that version to every watcher. A committed-mode reader receives the change with status `Committed` at that point. A change staged by another turn after the write captured its entries waits for the following write, so committed readers never observe it early.

## Watch values and collections

<xref:Orleans.Journaling.DurableStateWatchExtensions> provides `WatchAsync` for each built-in state type:

| State | Event type | Change kinds |
| --- | --- | --- |
| <xref:Orleans.Journaling.IDurableValue`1> | <xref:Orleans.Journaling.ValueChange`1> | The latest value |
| <xref:Orleans.Journaling.IDurableDictionary`2> | <xref:Orleans.Journaling.DictionaryChange`2> | `Resync`, `Set`, `Removed`, `Cleared`, `Watermark` |
| <xref:Orleans.Journaling.IDurableList`1> | <xref:Orleans.Journaling.ListChange`1> | `Resync`, `Added`, `Set`, `Inserted`, `RemovedAt`, `Cleared`, `Watermark` |
| <xref:Orleans.Journaling.IDurableSet`1> | <xref:Orleans.Journaling.SetChange`1> | `Resync`, `Added`, `Removed`, `Cleared`, `Watermark` |
| <xref:Orleans.Journaling.IDurableQueue`1> | <xref:Orleans.Journaling.QueueChange`1> | `Resync`, `Enqueued`, `Dequeued`, `Cleared`, `Watermark` |

A value watch begins with the current value and then delivers the latest value after each change. A slow reader receives the newest value and skips intermediate values, so a value watch retains no backlog.

A collection watch begins with a `Resync` event whose `Snapshot` holds the complete contents, followed by each change in the order it was made. Apply the changes to the snapshot to maintain a local copy. In committed mode, the initial snapshot is delivered once it is durable. Each reader buffers a bounded number of undelivered changes. When a reader falls behind, the stream replaces its backlog with a new `Resync` snapshot and continues with later changes, so memory grows with the number of readers rather than with their backlog. In speculative mode, a `Watermark` event reports that staged changes with versions up to and including its `Version` are committed.

A collection which nobody watches publishes nothing and allocates nothing for observation. The manager creates its observer state when the first stream begins.

## Read a durable log

<xref:Orleans.Journaling.IDurableLog`1>, created with <xref:Orleans.Journaling.DurableStateManagerExtensions.GetOrAddLog*>, assigns each appended entry the next value of a durable, monotonically increasing sequence number:

- <xref:Orleans.Journaling.IDurableLog`1.Append*> stages an entry and returns its sequence number.
- <xref:Orleans.Journaling.IDurableLog`1.FirstSequence>, <xref:Orleans.Journaling.IDurableLog`1.NextSequence>, and <xref:Orleans.Journaling.IDurableLog`1.CommittedSequence> describe the retained, appended, and durable entries. Serve paged history from entries before `CommittedSequence`.
- <xref:Orleans.Journaling.IDurableLog`1.TrimBefore*> discards old entries to bound retention. Sequence numbers are never reused.

<xref:Orleans.Journaling.IDurableLog`1.ReadAsync*> reads retained entries from a sequence number and then waits for new ones. Readers re-read entries from the log at their position instead of buffering them. When the position precedes the first retained entry, or follows the end of a log which was deleted, the stream delivers <xref:Orleans.Journaling.LogEventKind.Reset> with the first retained sequence number and continues from there. Because sequence numbers are stable, a sequence number is a complete replay checkpoint.

A speculative reader receives staged entries immediately, followed by a `Watermark` event whose `Sequence` is the end of the committed entries. Pass the latest watermark sequence when the reader reconnects.

## Stream drafts

<xref:Orleans.Journaling.IDurableLog`1.BeginDraft*> opens an <xref:Orleans.Journaling.IDurableLogDraft`1>:

- `Write` delivers a volatile item to speculative readers immediately. The draft holds its items in memory only while it is open.
- `Commit` closes the draft and appends its durable entry. Speculative readers receive `DraftCommitted` with the entry's sequence number, followed by the staged entry.
- `Abandon` closes the draft without an entry and reports `DraftAbandoned` with a reason. Disposing an open draft abandons it, so a failed operation, such as a model stream which fails mid-response, releases its draft.

Committed-mode readers never observe drafts. A speculative reader which begins while a draft is open receives the draft's items from position zero.

Each <xref:Orleans.Journaling.LogDraftId> includes an identifier for the activation which opened the draft. A reconnecting reader passes its committed sequence and the draft it was viewing. If that draft is still open, the stream repeats its items from position zero. Otherwise, the stream reports `DraftAbandoned` before continuing, including when a new activation recovered the grain after the previous one was lost. The reader then reads committed entries from its position, which contain any content the draft committed. Regenerate a lost response from the last committed entry.

This client renders committed history and in-progress drafts and tracks its resume position:

:::code language="csharp" source="../../snippets/compiled/Grains/JournalingObservationSnippets.cs" id="conversation_client":::

## Failure and shutdown

A failed journal write fences the manager and requests grain deactivation. Watch streams then fault with an <xref:System.InvalidOperationException> whose inner exception is the storage failure, and committed readers never receive the changes the failed write contained. Readers reconnect to the new activation, which recovers the durable state. When the manager stops normally, streams complete. Cancelling a stream's token ends that stream.

Deleting the grain's journal delivers a `Resync` snapshot of the empty collection, or `Reset` for a log, and abandons open drafts.

## Discover registered state

Diagnostic tools enumerate a manager's registered state with <xref:Orleans.Journaling.IJournaledStateManager.GetStateMachines*>, which returns each name and instance:

:::code language="csharp" source="../../snippets/compiled/Grains/JournalingObservationSnippets.cs" id="state_discovery":::

Cast an instance to its state contract, such as `IDurableDictionary<TKey, TValue>`, to watch it.
