---
title: JournaledGrain diagnostics
description: Learn how to use JournaledGrain diagnostics in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# JournaledGrain diagnostics

## Monitor connection errors

By design, log-consistency providers are resilient under connection errors (including connections to storage and connections between clusters). However, merely tolerating errors isn't enough. Applications usually need to monitor such issues and bring them to an operator's attention if they are serious.

`JournaledGrain` subclasses can override the following methods to receive notifications when connection errors are observed and when those errors resolve:

```csharp
protected override void OnConnectionIssue(
    ConnectionIssue issue)
{
    /// handle the observed error described by issue
}

protected override void OnConnectionIssueResolved(
    ConnectionIssue issue)
{
    /// handle the resolution of a previously reported issue
}
```

<xref:Orleans.LogConsistency.ConnectionIssue> is an abstract class with several common fields describing the issue, including how many times it has been observed since the last successful connection. Subclasses define the actual type of connection issue. Connection issues are categorized into types, such as <xref:Orleans.EventSourcing.Common.PrimaryOperationFailed> or <xref:Orleans.LogConsistency.NotificationFailed>, and sometimes have extra keys (like <xref:Orleans.EventSourcing.Common.NotificationFailed.RemoteCluster>) that further narrow the category.

If the same category of issue happens multiple times (for example, you keep getting a `NotificationFailed` targeting the same `RemoteCluster`), Orleans reports it each time via <xref:Orleans.EventSourcing.JournaledGrain`2.OnConnectionIssue*>. Once this category of issue resolves (for example, you finally succeed in sending a notification to that `RemoteCluster`), Orleans calls <xref:Orleans.EventSourcing.JournaledGrain`2.OnConnectionIssueResolved*> once, with the same `issue` object last reported by `OnConnectionIssue`. Connection issues and their resolutions for independent categories are reported independently.

## Simple statistics

We currently offer simple support for basic statistics (in the future, we'll likely replace this with a more standard telemetry mechanism). You can enable or disable statistics collection for a `JournaledGrain` by calling:

```csharp
void EnableStatsCollection()
void DisableStatsCollection()
```

Retrieve the statistics by calling:

```csharp
LogConsistencyStatistics GetStats()
```
