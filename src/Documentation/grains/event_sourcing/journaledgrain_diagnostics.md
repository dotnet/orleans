---
layout: page
title: JournaledGrain Diagnostics
---

# JournaledGrain Diagnostics


## Monitoring Connection Errors

By design, log consistency providers are resilient under connection errors (including both connections to storage, and connections between clusters). But just tolerating errors is not enough, as applications usually need to monitor any such issues, and bring them to the attention of an operator if they are serious.

JournaledGrain subclasses can override the following methods to receive notifications when there are connection errors observed, and when those errors are resolved:

```csharp
protected override void OnConnectionIssue(ConnectionIssue issue) 
{
    /// handle the observed error described by issue             
}
protected override void OnConnectionIssueResolved(ConnectionIssue issue) 
{
    /// handle the resolution of a previously reported issue             
}
```
    
`ConnectionIssue` is an abstract class, with several common fields describing the issue, including how many times it has been observed since the last time connection was successful. The actual type of connection issue is defined by subclasses. Connection issues are categorized into types, such as `PrimaryOperationFailed` or `NotificationFailed`, and sometimes have extra keys (such as `RemoteCluster`) that further narrow the category.

If the same category of issue happens several times (for example, we keep getting a `NotificationFailed` that targets the same `RemoteCluster`), it is reported each time by `OnConnectionIssue`. Once this category of issue is resolved (for example, we are finally successful with sending a notification to this `RemoteCluster`), then `OnConnectionIssueResolved` is called once, with the same `issue` object that was last reported by `OnConnectionIssue`. Connection issues, and their resolution, for independent categories, are reported independently.

## Simple Statistics

We currently offer a simple support for basic statistics (in the future, we will probably replace this with a more standard telemetry mechanism).
Statistics collection can be enabled or disabled for a JournaledGrain by calling

```csharp
void EnableStatsCollection()
void DisableStatsCollection()
```

The statistics can be retrieved by calling

 ```csharp
LogConsistencyStatistics GetStats()
```
