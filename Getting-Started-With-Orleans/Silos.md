---
layout: page
title: Silos
---
{% include JB/setup %}

An Orleans silo is a runtime container managing grain hosting and execution. 
Typically, one silo will be run per machine.

A number of silos can work together to form an Orleans cluster.

More silos can be added to a cluster dynamically in order to scale-out that cluster.

The Orleans runtime manages resilience in case a silo is removed from the cluster due to a machine crash or similar failure mode.

## Next
Next we look at what a client is and how it interacts in the Orleans architecture.

[Clients](Clients)