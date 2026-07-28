---
title: Implementation details
description: Explore the various implementation details in .NET Orleans.
ms.date: 05/23/2025
ms.topic: overview
---

# Implementation details overview

## [Orleans lifecycle](orleans-lifecycle.md)

Some Orleans behaviors are sufficiently complex that they need ordered startup and shutdown. To address this, Orleans introduced a general component lifecycle pattern.

## [Messaging delivery guarantees](messaging-delivery-guarantees.md)

Orleans messaging delivery guarantees are **at-most-once** by default. Optionally, if you configure retries upon timeout, Orleans provides at-least-once delivery instead.

## [Scheduler](scheduler.md)

The Orleans Scheduler is a component within the Orleans runtime responsible for executing application code and parts of the runtime code to ensure single-threaded execution semantics.

## [Cluster management](cluster-management.md)

Orleans provides cluster management via a built-in membership protocol, sometimes referred to as Silo Membership. The goal of this protocol is for all silos (Orleans servers) to agree on the set of currently alive silos, detect failed silos, and allow new silos to join the cluster.

## [Streams implementation](streams-implementation/index.md)

This section provides a high-level overview of the Orleans Stream implementation. It describes concepts and details not visible at the application level.

## [Load balancing](load-balancing.md)

Load balancing, in a broad sense, is one of the pillars of the Orleans runtime.

## [Unit testing](testing.md)

This section shows how to unit test your grains to ensure they behave correctly.
