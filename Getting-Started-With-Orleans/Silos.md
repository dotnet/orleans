---
layout: page
title: Silos
---
{% include JB/setup %}

An Orleans silo is a runtime container managing grain hosting and execution. Typically, one silo will be run per machine.

A number of silos can work together to form an Orleans cluster.

More silos can be added to a cluster dynamically in order to scale-out that cluster.

The Orleans runtime managed resilience in case of a silo being removed from the cluster due to machine crash or similar failure mode.