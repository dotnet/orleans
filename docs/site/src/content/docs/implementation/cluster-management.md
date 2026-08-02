---
title: Cluster membership protocol
description: Understand Orleans membership storage, failure detection, ordered views, and death-vote invariants.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Cluster membership protocol

Cluster membership answers one question for the rest of the runtime: which silo identities are members of the current view, and what is each member's status? Orleans combines a durable <xref:Orleans.IMembershipTable> with direct peer probes. The table provides coordination and ordered views; probes measure the communication path that Orleans actually uses.

## Identity, status, and views

A silo identity includes its advertised endpoint and a generation value, so a restarted process at the same endpoint is a new identity. Its status progresses through `Created`, `Joining`, `Active`, and a terminating status (`ShuttingDown`, `Stopping`, or `Dead`).

Each successful membership-table mutation advances a version. `MembershipTableManager` publishes immutable snapshots through `ClusterMembershipService`; consumers ignore older versions. Directory ownership, gateway discovery, and failure recovery therefore observe a monotonically ordered sequence of views even if notifications arrive out of order.

```mermaid
flowchart LR
    Table[(IMembershipTable)]
    Manager[MembershipTableManager]
    Service[ClusterMembershipService]
    Agent[MembershipAgent]
    Health[ClusterHealthMonitor]
    Consumers[Directory, placement, gateways]

    Agent -->|join and IAmAlive writes| Manager
    Health -->|death votes| Manager
    Manager <-->|versioned reads and writes| Table
    Manager --> Service
    Service --> Consumers
```

## Joining

A starting silo writes its row, becomes `Joining`, and validates two-way connectivity with active members before becoming `Active`. This prevents a partitioned process from silently joining one side of a cluster.

The periodic `IAmAlive` value is not the peer heartbeat. It is a timestamp written to the membership row for diagnostics and startup disaster recovery. A sufficiently stale active row can be ignored during the joining connectivity check, allowing a cluster to recover after all processes were lost without cleanly declaring each other dead.

## Failure detection and death votes

Active silos monitor peers selected from the membership view. `ClusterHealthMonitor` sends probes over silo-to-silo messaging, tracks consecutive failures, and can use indirect probes to distinguish a failed target from an unhealthy observer. A failed monitor writes a timestamped vote into the target's membership row.

```mermaid
sequenceDiagram
    participant A as Monitoring silo A
    participant B as Target silo B
    participant C as Indirect probe silo C
    participant T as Membership table

    A->>B: Probe
    B--xA: No response
    A->>C: Probe B indirectly
    C->>B: Probe
    B--xC: No response
    C-->>A: Negative acknowledgement
    A->>T: Read B and fresh votes
    A->>T: Compare-and-swap vote/status + view version
    T-->>A: New ordered membership view
```

Declaring a member dead requires enough unexpired votes from distinct observers. The read-modify-write is protected by the membership table's version or ETag. A conflicting update causes the writer to reread and reevaluate; it must not overwrite a newer view.

Once a row is `Dead`, that identity never returns to `Active`. If the process was only partitioned, it terminates when it learns that the cluster declared it dead. Its host can restart it with a new generation.

## Default settings

The defaults are defined by [`ClusterMembershipOptions`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core/Configuration/Options/ClusterMembershipOptions.cs):

| Option | Default | Protocol role |
| --- | ---: | --- |
| `NumProbedSilos` | 10 | Number of peers monitored by each silo |
| `ProbeTimeout` | 5 seconds | Baseline direct probe timeout |
| `NumMissedProbesLimit` | 3 | Failed probes before a death vote |
| `NumVotesForDeathDeclaration` | 2 | Fresh votes required to mark a member dead |
| `DeathVoteExpirationTimeout` | 2 minutes | Lifetime of a death vote |
| `TableRefreshTimeout` | 1 minute | Fallback membership-table refresh period |
| `IAmAliveTablePublishTimeout` | 30 seconds | Membership-row liveness timestamp period |

These values are protocol parameters, not independent timers: indirect probing, local health, scheduling delays, and table contention all affect observed detection time. The runtime increases probe tolerance when `LocalSiloHealthMonitor` detects thread-pool delay, timer delay, or other local distress, reducing false accusations from an unhealthy observer.

## Membership-table contract

An `IMembershipTable` implementation is more than a list of endpoints. It must support:

- insertion of a new silo row;
- optimistic, conditional update of a silo row;
- atomic advancement of the table version with a row mutation;
- reads which return rows and the corresponding version;
- periodic `IAmAlive` updates; and
- durable availability appropriate for cluster coordination.

Table unavailability favors safety over liveness. Existing silos can continue processing calls, but they cannot durably admit a member or declare a failed member dead. A provider must not synthesize successful updates when its backing store is unavailable.

Official providers adapt transactions, ETags, lightweight transactions, or compare-and-swap primitives to this contract. Provider selection and operational setup belong in the [deployment documentation](../deployment/index.md); the extension architecture is covered by [provider authoring](provider-authoring.md).

## Protocol consumers

Membership is deliberately separate from the services which consume it:

- `LocalGrainDirectory` adjusts consistent-hash ownership after view changes.
- the experimental distributed directory runs an explicit range-transfer protocol.
- placement removes unavailable or overloaded candidates.
- clients refresh the gateway list.
- persistent-stream queue balancers redistribute queue responsibility.
- activation balancing protocols stop exchanging work with failed members.

This separation lets those services add stronger invariants without expanding the membership-table transaction.

## Source and tests

- [`MembershipAgent`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/MembershipService/MembershipAgent.cs) drives joining and active-state transitions.
- [`MembershipTableManager`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/MembershipService/MembershipTableManager.cs) coordinates table updates and death declarations.
- [`ClusterHealthMonitor`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/MembershipService/ClusterHealthMonitor.cs) owns peer monitoring.
- [`MembershipAgentTests`](https://github.com/dotnet/orleans/blob/main/test/Orleans.Core.Tests/Membership/MembershipAgentTests.cs) exercise startup connectivity.
- [`MembershipTableManagerTests`](https://github.com/dotnet/orleans/blob/main/test/Orleans.Core.Tests/Membership/MembershipTableManagerTests.cs) cover vote expiry and status changes.
