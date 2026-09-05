---
title: Monitor runtime dissemination
description: Monitor convergence, retry pressure, repair, backpressure, and fallback behavior in Orleans runtime dissemination.
ms.date: 09/04/2026
ms.topic: concept-article
---

# Monitor runtime dissemination

Runtime dissemination accelerates convergence of membership, deployment-load, and manifest-summary state. Monitor its progress and failure signals alongside the authoritative membership, placement, manifest, messaging, and networking signals.

The instruments use the standard `Microsoft.Orleans` meter:

```dotnetcli
dotnet-counters monitor -n <ProcessName> --counters Microsoft.Orleans
```

See [Runtime state dissemination](../../implementation/runtime-dissemination.md) for protocol invariants and failure semantics and the [metrics catalog](metrics-catalog.md#runtime-dissemination) for the complete instrument reference.

## Build a convergence dashboard

Use rates over the same interval and split by `namespace` where available:

| Signal | Interpretation |
|---|---|
| Broadcast sent and received | Tree traffic reaches peers. Compare cluster-wide rates during active publication. |
| Values sent, received, and applied | Receipt measures delivered work. Break apply `result` into applied, duplicate, obsolete, and rejected values. |
| Broadcast scheduled with `reason=retry` | A peer pump retained work after incomplete delivery or repair. |
| Broadcast send failures | `timeout` indicates the hop lifetime expired; `error` indicates an RPC or transport failure. |
| Anti-entropy exchanges and values | Periodic repair remains active and returns bounded work. |
| Anti-entropy failures | Selected repair peers timed out or failed. Rotating peer selection should let later rounds use other peers. |
| Publications | `accepted` measures work admitted to peer pumps. `rejected` identifies disabled, unavailable, invalid, over-budget, or queue-rejected publications which retain the component's direct path. |
| Queue admission rejected | One namespace reached its per-peer distinct-key bound. |
| Pump failures | `recovered` records an isolated iteration failure; `permanent` records a pump whose waiters were failed explicitly. |

The `truncated=true` anti-entropy series means a response exhausted an item or byte budget while more candidates remained. Occasional truncation is bounded continuation. Sustained truncation with low applied-value throughput indicates that budgets, payload sizes, or publication churn are limiting convergence.

## Alert on sustained loss of progress

Page on user-visible impact first. Dissemination signals identify a contributing runtime path:

- Alert on permanent pump failures because one destination has stopped accepting broadcast work until its peer state is recreated.
- Alert on sustained queue admission rejection because updates for new keys are using fallback paths or waiting for capacity.
- Correlate broadcast and anti-entropy failure rates with `orleans-messaging-sent-failed`, socket churn, membership changes, and deployment events.
- Compare rejected publications with the runtime component's update rate. A rollout can temporarily increase direct-path use while peer capability evidence is established.
- Investigate sustained retry scheduling when successful broadcasts and applied values remain flat.

Timeout and retry spikes during a rolling restart or network partition can be expected. Healthy recovery produces successful exchanges, declining retry and failure rates, and continued applied or duplicate outcomes after connectivity returns.

## Diagnose by symptom

### One silo has persistent retries

Compare that instance's send failures with messaging and socket metrics. Inspect `Orleans.Runtime.Dissemination.DisseminationBroadcastQueue` logs for the peer and retry delay. A permanent pump failure is logged at Error; a recovered iteration failure is logged at Warning.

### Queue admission is rejected

Identify the `namespace` and compare update cardinality with <xref:Orleans.Configuration.DisseminationNamespaceOptions.MaxPendingItemCount>. Increase the limit only after confirming the process has memory headroom and the destination can drain work. Broadcast item and byte limits still bound each transmission.

### Anti-entropy remains truncated

Compare returned values with <xref:Orleans.Configuration.DisseminationOptions.MaxBatchItems>, <xref:Orleans.Configuration.DisseminationOptions.MaxBatchBytes>, and the namespace payload limit. Persistent cursors continue from the bounded response across later exchanges, so verify that exchanges and applied values continue rather than alerting on truncation alone.

### Rejected publications increase

Break down by `namespace` and `reason`. Validate peer version compatibility, payload limits, and namespace enablement. `queue-rejected` identifies a full or stopping broadcast queue; correlate it with queue admission rejection and silo lifecycle activity. Deployment-load, membership, and manifest integrations retain direct paths, so also inspect their component-specific logs and metrics for the authoritative outcome.

## DiagnosticListener events

The `Microsoft.Orleans.Dissemination` diagnostic listener exposes detailed, opt-in events:

| Event | Use |
|---|---|
| `Dissemination.ValueApply` | Inspect one value's namespace, key, version transition, peer, result, and payload size. |
| `Dissemination.PayloadDrop` | Inspect a value rejected by a payload guard. |
| `Dissemination.BroadcastScheduled` | Observe immediate, coalesced, priority, and retry scheduling with due time and attempt. |
| `Dissemination.QueueAdmissionRejected` | Observe the peer, namespace, configured limit, and bounded rejection reason. |

Value events include keys and peer addresses. Enable them for a time-limited investigation and apply the deployment's telemetry data and retention policy. Metrics intentionally omit keys and peer addresses to keep long-term cardinality bounded.
