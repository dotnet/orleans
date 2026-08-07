---
title: JournaledGrain diagnostics
description: Monitor log-consistency connection issues and statistics.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Journaled grain diagnostics

## Connection issues

Override these callbacks to observe persistence or protocol failures and their recovery:

```csharp
protected override void OnConnectionIssue(ConnectionIssue issue)
{
    // Record the issue category, retry count, and exception.
}

protected override void OnConnectionIssueResolved(ConnectionIssue issue)
{
    // Clear or resolve the corresponding health signal.
}
```

<xref:Orleans.EventSourcing.Common.PrimaryOperationFailed> identifies failed access to the provider's primary storage. Concrete issue types include failures to read or update state storage, log storage, or custom storage. Repeated failures in one category produce repeated issue callbacks; resolution produces one corresponding resolved callback.

These callbacks are diagnostics, not a replacement for observing failed or delayed grain calls. Avoid throwing from them; Orleans catches and logs callback exceptions.

## Per-grain statistics

Enable collection with <xref:Orleans.EventSourcing.JournaledGrain`2.EnableStatsCollection*>, retrieve a nullable <xref:Orleans.EventSourcing.LogConsistencyStatistics> value with <xref:Orleans.EventSourcing.JournaledGrain`2.GetStats*>, and stop collection with <xref:Orleans.EventSourcing.JournaledGrain`2.DisableStatsCollection*>.

Collection is opt-in and local to the journaled grain. Use it for focused diagnosis rather than as the sole production telemetry system. Combine it with provider metrics, storage-service metrics, Orleans logs, and call latency/error telemetry.
