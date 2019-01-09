---
layout: page
title: Implementation Details
---
# Implementation Details Overview

## [Orleans Lifecycle](orleans_lifecycle.md)

Some Orleans behaviors are sufficiently complex that they need ordered startup and shutdown.
To address this, a general component lifecycle pattern has been introduced.

## [Messaging Delivery Guarantees](messaging_delivery_guarantees.md)

Orleans messaging delivery guarantees are **at-most-once**, by default.
Optionally, if configured to do retries upon timeout, Orleans provides at-least-once delivery instead.

## [Scheduler](scheduler.md)

Orleans Scheduler is a component within the Orleans runtime responsible for executing application code and parts of the runtime code to ensure the single threaded execution semantics.

## [Cluster Management](cluster_management.md)

Orleans provides cluster management via a built-in membership protocol, which we sometimes refer to as Silo Membership.
The goal of this protocol is for all silos (Orleans servers) to agree on the set of currently alive silos, detect failed silos, and allow new silos to join the cluster.

## [Streams Implementation](streams_implementation.md)

This section provides a high level overview of Orleans Stream implementation.
It describes concepts and details that are not visible on the application level.

## [Load Balancing](load_balancing.md)

Load balancing, in a broad sense, is one of the pillars of the Orleans runtime.

## [Streams Implementation](streams_implementation.md)

This section shows how to unit test your grains to make sure they behave correctly.