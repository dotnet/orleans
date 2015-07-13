---
layout: page
title: Core Concepts
---
{% include JB/setup %}


## Overview of the Orleans Architecture

Orleans is based on an asynchronous, single-threaded actor model. 
The units of distribution, actors, encapsulating data and computation, are called grains. 
Grains interact by passing asynchronous messages, whose return values are represented in the code as promises.

Orleans has two primary architectural goals:

 1. Make it easy for developers with no experience in distributed systems to build cloud-scale applications. Ensure that those systems scale across multiple orders of magnitude of load without requiring extensive re-design or re-architecture. 
 2. To meet these goals, we have constrained the programming model in order to guide developers down a path of best practices leading to scalable applications. In some cases, such as persistence, we have removed an entire aspect from the explicit programming model and left the functionality to the runtime, to ensure a scalable approach.

## Actors

Orleans starts with a basic model of actors that interact through asynchronous message passing. 
Actors are isolated single-threaded components that encapsulate both state and behavior. 
They are similar to objects, and therefore should be natural to any developer. 
Asynchronous messaging differs greatly from synchronous method calls, but experience has shown that purely synchronous systems do not scale well; in this case we have traded familiarity for scalability.

To avoid confusion with other systems, actors in Orleans are called grains.
Messages are represented by special methods on the .NET interface for a grain type. 
The methods are regular .NET functions, except that they must return a promise, a construct that represents a value that will become available at some future time. 
Grains are single-threaded and process messages one at a time, so that developers do not need to deal with locking or other concurrency issues.

## Virtual Actors

Unlike actors in other systems such as Erlang or Akka, Orleans grains are virtual actors. 
The Orleans runtime manages the location and activation of grains similarly to the way that the virtual memory manager of an operating system manages memory pages: it activates a grain by creating an in-memory copy (an activation) on a server, and later it may deactivate that activation if it hasn't been used for some time. 
If a message is sent to the grain and there is no activation on any server, then the runtime will pick a location and create a new activation there.

Because grains are virtual, they never fail, even if the server that currently hosts all of their activations fails. 
This eliminates the need to test to see if a grain exists, as well as the need to track failures and recreate grains as needed; the Orleans runtime does all this automatically. 
Application code can create a reference to a grain.

##Next
[Asynchrony and Tasks](Asynchrony-and-Tasks)