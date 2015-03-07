---
layout: page
title: Global-Single-Instance Grains
---

[!include[](../../warning-banner.md)]

### Grain Coordination Attributes

Developers can indicate when and how clusters should coordinate their grain directories with respect to a particular grain class. The  `[GlobalSingleInstance]` attribute means we want the same behavior as as when running Orleans in a single global cluster: that is, route all calls to a single activation of the grain. Conversely, the `[OneInstancePerCluster]` attribute indicates that each cluster can have its own independent activation. This is appropriate if communication between clusters is undesired.

The attributes are placed on grain implementations. For example: 
```csharp
using Orleans.MultiCluster;

[GlobalSingleInstance]
public class MyGlobalGrain : Orleans.Grain, IMyGrain {
   ...
}

[OneInstancePerCluster]
public class MyLocalGrain : Orleans.Grain, IMyGrain {
   ...
}
```

If a grain class does not specify either one of those attributes, it defaults to `[OneInstancePerCluster]`, or `[GlobalSingleInstance]` if the  configuration parameter `UseGlobalSingleInstanceByDefault` is set to true.

#### Protocol for Global-Single-Instance Grains

When a global-single-instance (GSI) grain is accessed, and no activation is known to exist, a special GSI activation protocol is executed before activating a new instance. Specifically, a request is sent to all other clusters in the current [multi-cluster configuration](MultiClusterConfiguration.md) to check if they already have an activation for this grain. If all responses are negative, a new activation is created in this cluster. Otherwise, the remote activation is used (and a reference to it is cached in the local directory).

#### Protocol for One-Instance-Per-Cluster Grains

There is no inter-cluster communication for One-Instance-Per-Cluster grains. They simply use the standard Orleans mechanism independently within each cluster. Inside the Orleans framework itself, the following grain classes are marked with the `[OneInstancePerCluster]` attribute: `ManagementGrain`, `GrainBasedMembershipTable`,  and `GrainBasedReminderTable`. 

#### Doubtful Activations

If the GSI protocol does not receive conclusive responses from all clusters after 3 retries (or whatever number is specified by the configuration parameter `GlobalSingleInstanceNumberRetries`), it creates a new local "doubtful" activation optimistically, favoring availability over consistency.

Doubtful activations may be duplicates (because some remote cluster that did not respond during the GSI protocol activation may nevertheless have  an activation of this grain). Therefore, periodically every 30 seconds (or whatever interval is specified by the configuration parameter `GlobalSingleInstanceRetryInterval`) the GSI protocol is run again for all doubtful activations. This ensures that once communication between clusters is restored, duplicate activations can be detected and removed. 

