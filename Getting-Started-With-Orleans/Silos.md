---
layout: page
title: Silos
---
{% include JB/setup %}

An Orleans silo is a server that hosts and executes Orleans grains. It has one listening port for silo-to-silo messaging and another for client-to-silo messaging. Typically, one silo is run per machine.

A number of silos can work together to form an Orleans cluster. A cluster has a shared membership store that is kept up-to-date by member silos.
Silos learn about each others' status by reading the shared store. At any time, a silo can join a cluster by registering in a the shared store. Therefore, the cluster can be scaled-out dynamically at runtime.

Orleans provides resilience and availability by removing unresponsive silos from the cluster.

For an in-depth detailed documentation of how Orleans manages a cluster, read about [Cluster Management](/orleans/Runtime-Implementation-Details/Cluster-Management).

## Next
Next we look at what a client is and how it interacts in the Orleans architecture.

[Clients](Clients)