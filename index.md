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
        <a class="btn btn-lg btn-primary" href="https://github.com/dotnet/orleans">Go to the Orleans Repo</a> 
      </div>
    </div>
</div>

### [Orleans Overview](/Documentation/index.html)
Orleans is a framework that provides a straightforward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. 
Go here to learn more about what Orleans is.

### [Core Concepts](/Documentation/core_concepts/index.html)
Orleans is a framework that provides a straightforward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. 
This section contains information about the fundamental components of Orleans.

### [Grains](/Documentation/grains/index.html)
Grains are the key primitives of the Orleans programming model. They are the building blocks of an Orleans application, and they are atomic units of isolation, distribution, and persistence. Grains are objects that represent application entities. Just like in the classic Object Oriented Programming, a grain encapsulates state of an entity and encodes its behavior in the code logic. 
This section contains in-depth information about grains.

### [Clusters and Clients](/Documentation/clusters_and_clients/index.html)
The term "Client" or sometimes "Grain Client" is used for application code that interacts with grains but itself is not part of a grain logic. Client code runs outside of the cluster of Orleans servers called silos where grains are hosted. Hence, a client acts as a connector or conduit to the cluster and to all grains of the application.
Go here to learn more about clusters and clients, specifically. 

### [Deployment](/Documentation/deployment/index.html)
A typical Orleans application consists of a cluster of server processes (silos) where grains live, and a set of client processes, usually web servers, that receive external requests, turn them into grain method calls, and return results back. Hence, the first thing one needs to do to run an Orleans application is to start a cluster of silos.


### [Tutorials and Samples](/Documentation/tutorials_and_samples/index.html)
Start with the MathGrains tutorial to learn how to create and deploy an Orleans app on your local machine. 
You can use this and other samples as starting points for your own application. 

### [Resources](/Documentation/resources/index.html)
 Information about contributing, links to articles about Orleans, and the sections about Migration, and Presentations.


---
##Archive
Links to 1.5 documentation and tutorials could go here...
---

### Links

Discuss your Orleans questions in the [gitter chat room](https://gitter.im/dotnet/orleans).

Fork the code on the [GitHub Respository](https://github.com/dotnet/orleans).