---
layout: page
title: Silos
---


An Orleans silo is a server that hosts and executes Orleans grains. It has one listening port for silo-to-silo messaging and another for client-to-silo messaging. Typically, one silo is run per machine.

## Cluster
A number of silos can work together by forming an Orleans cluster. Orleans runtime fully automates cluster management.
All silos use a shared membership store that is updated dynamically and helps coordinate cluster management.
Silos learn about each others' status by reading the shared store. At any time, a silo can join a cluster by registering in a the shared store. This way the cluster can can scale-out dynamically at runtime.
Orleans provides resilience and availability by removing unresponsive silos from the cluster.

For an in-depth detailed documentation of how Orleans manages a cluster, read about [Cluster Management](/orleans/Runtime-Implementation-Details/Cluster-Management).

## Next
Next we look at what a client is and how it interacts in the Orleans architecture.

[Clients](Clients.md)
