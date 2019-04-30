---
documentType: index
title: Microsoft Orleans
tagline: A straightforward approach to building distributed, high-scale applications in .NET
---
<style>
.subtitle {
    font-size:20px;
}
.main_logo {
    width:60%
}
.jumbotron{
    text-align: center;
}
</style>

<div class="jumbotron">
    <div class="container">
      <img src="images/logo.svg" class="main_logo" />
      <h1 class="title"><small class="subtitle">A straightforward approach to building distributed, high-scale applications in .NET</small></h1>
      <div class="options">
        <a class="btn btn-lg btn-primary" href="https://github.com/dotnet/orleans">Go to the Orleans Repository</a> 
      </div>
    </div>
</div>

### This documentation is for the 2.0 release
Orleans 2.0 is a significant overhaul from the 1.x versions.
The 2.0 release is cross-platform via [.NET Standard 2.0](https://github.com/dotnet/standard/blob/master/docs/versions/netstandard2.0.md) and [.NET Core](https://www.microsoft.com/net).
It has a more modular and flexible structure due to heavy use of Dependency Injection, a modern configuration API, and a revamped provider model.

Orleans 2.0 still supports most of the 1.x API via optional legacy packages.
Orleans 1.5 will continue to be supported for some time, but 2.0 is where all the investments are going.
For 1.5 Documentation and Tutorials, refer to the respective sections that are snapshots of the documentation as of the 2.0 release.

### [Overview](Documentation/index.md)
Orleans is a framework that provides a straightforward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. 

### [Core Concepts](Documentation/core_concepts/index.md)
Orleans is a framework that provides a straightforward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. 
This section contains information about the fundamental components of Orleans.

### [Grains](Documentation/grains/index.md)
Grains are the key primitives of the Orleans programming model. They are the building blocks of an Orleans application, and they are atomic units of isolation, distribution, and persistence. Grains are objects that represent application entities. Just like in the classic Object Oriented Programming, a grain encapsulates state of an entity and encodes its behavior in the code logic. 
This section contains in-depth information about grains.

### [Clusters and Clients](Documentation/clusters_and_clients/index.md)
The term "Client" or sometimes "Grain Client" is used for application code that interacts with grains but itself is not part of a grain logic. Client code runs outside of the cluster of Orleans servers called silos where grains are hosted. Hence, a client acts as a connector or conduit to the cluster and to all grains of the application.
Go here to learn more about clusters and clients, specifically.

### [Deployment](Documentation/deployment/index.md)
A typical Orleans application consists of a cluster of server processes (silos) where grains live, and a set of client processes, usually web servers, that receive external requests, turn them into grain method calls, and return results back. Hence, the first thing one needs to do to run an Orleans application is to start a cluster of silos.

### [Implementation Details](Documentation/implementation/index.md)
This section has information about [Orleans Lifecycle](Documentation/implementation/orleans_lifecycle.md), [Messaging Delivery Guarantees](Documentation/implementation/messaging_delivery_guarantees.md), [Scheduler](Documentation/implementation/scheduler.md), [Cluster Management](Documentation/implementation/cluster_management.md),  [Streams Implementation](Documentation/implementation/streams_implementation.md), and [Load Balancing](Documentation/implementation/load_balancing.md).

### [Streaming](Documentation/streaming/index.md)
Streaming extensions provide a set of abstractions and APIs that make thinking about and working with streams simpler and more robust. Streaming extensions allow developers to write reactive applications that operate on a sequence of events in a structured way.

### [Tutorials and Samples](Documentation/tutorials_and_samples/index.md)
Start with the MathGrains tutorial to learn how to create and deploy an Orleans app on your local machine. 
You can use this and other samples as starting points for your own application. 

### [Resources](Documentation/resources/index.md)
Information about contributing, links to articles about Orleans, and the sections about Migration, and Presentations.

Discuss your Orleans questions in the [gitter chat room](https://gitter.im/dotnet/orleans).

Fork the code on the [GitHub Respository](https://github.com/dotnet/orleans).

Read the [Orleans Blog](blog/index.md)
