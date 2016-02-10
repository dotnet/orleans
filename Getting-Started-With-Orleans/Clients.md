---
layout: page
title: Clients
---
{% include JB/setup %}

## Orleans and Client Code

An Orleans application consists of two distinct parts: the Orleans (grain based) part, and the client part.

The Orleans part is comprised of application grains hosted by Orleans Runtime servers called silos.
Grain code is executed by the runtime under scheduling restrictions and guarantees inherent in the Orleans programming model, detailed previously.

The client part, usually a web front-end, connects to the Orleans part via a thin layer of Orleans Client library that enables communication of the client code with grains hosted by the Orleans part via grain references.
The client part in this context means a client to the Orleans part, but it can run as part of a client or server applications.

For example, an ASP.NET application running on a web server can be a client part of an Orleans application.
The client part executes on top of the .NET thread pool, and is not subject to scheduling restrictions and guarantees of the Orleans Runtime.

## Next
Next we look how to receive asynchronous messages, or push data, from a grain.

[Client Observers](Observers)