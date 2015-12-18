---
layout: index
title: Microsoft Project Orleans
tagline: A straightforward approach to building distributed, high-scale applications in .NET
---
{% include JB/setup %}

Orleans is a framework that provides a straightforward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. 
It was created by Microsoft Research and designed for use in the cloud. 

Orleans has been used extensively in Microsoft Azure by several Microsoft product groups, most notably by 343 Industries as a platform for all of Halo 4 and Halo 5 cloud services, as well as by a number of other companies.

In Orleans, actors are called 'grains', and are described using an interface. Async methods are used to indicate which messages the actor can receive:

``` csharp
public interface IMyGrain : IGrainWithStringKey
{
    Task<string> SayHello(string name);
}
```

The implementation is executed inside the Orleans framework: 

``` csharp
public class MyGrain : IMyGrain
{
    public async Task<string> SayHello(string name)
    {
        return "Hello " + name;
    }
}
```

You can then send messages to the grain by creating a proxy object, and calling the methods:

``` csharp
var grain = GrainClient.GrainFactory.GetGrain<IMyGrain>("grain1");
grain.SayHello("World");
```

## Installation 

* **[Prerequisites](Prerequisites)**

* **[Orleans Tools for Visual Studio](https://visualstudiogallery.msdn.microsoft.com/36903961-63bd-4eec-9ca4-cf2319dc75f4)** or alternatively **[ETG.Orleans.Templates.VSIX](https://visualstudiogallery.msdn.microsoft.com/b61c87e7-0655-4a6e-8e4f-84192950e08c)** from BigPark

* **[Orleans NuGet Packages](NuGets)**

* **[What's new in Orleans?](What's-new-in-Orleans)** 
Coverage of the new and changed features since the original September 2014 preview release.

## Introduction and Tutorials

* **[Introduction to Orleans](Introduction)**

* **[Getting Started with Orleans](Getting-Started-With-Orleans)**
This section will introduce the basic concepts of the Orleans programming model as well as the basics of how to provide the runtime with the configuration parameters that it needs in order to function properly.
  
  * [Core Concepts](Getting-Started-With-Orleans/Core-Concepts)
  * [Asynchrony and Tasks](Getting-Started-With-Orleans/Asynchrony-and-Tasks)
  * [Grains](Getting-Started-With-Orleans/Grains) (a.k.a. Actors)
  * [Silos](Getting-Started-With-Orleans/Silos)
  * [Clients](Getting-Started-With-Orleans/Clients)
  * [Client Observers](Getting-Started-With-Orleans/Observers)
  * [Developing a Grain](Getting-Started-With-Orleans/Developing-a-Grain)
  * [Developing a Client](Getting-Started-With-Orleans/Developing-a-Client)
  * [Running the Application](Getting-Started-With-Orleans/Running-the-Application)



* **[Step-by-step Tutorials](Step-by-step-Tutorials)** - Complementing the "Getting Started" guide, this section will take you through a series of samples and hands-on tutorials that teach you the core concepts, as well as some advanced ones through hands-on experience.

* **[Deployment and Versioning of Dependencies](Deployment-and-Versioning-of-Dependencies)** 
How to deploy your application code and manage dependencies of it and of the Orleans runtime.

* **[Orleans Streams](Orleans-Streams)** 
This section describes the reactive programming with Orleans streams and the extensibility model for implementing new stream types.

* **[Frequently Asked Questions](Frequently-Asked-Questions)**

* **[Samples Overview](Samples-Overview)**

## Advanced Documentation

* **[Advanced Concepts](Advanced-Concepts)** 
There are a number of advanced development and deployment concepts that many Orleans developers do not need to know anything about. 
If you have already familiarized yourself with the basics, this section will allow you to go further into depth.

* **[Orleans Configuration Guide](Orleans-Configuration-Guide)**
Explains the key configuration parameters and how they should be used for several most typical usage scenarios. 

* **[Runtime Implementation Details](Runtime-Implementation-Details)** 
This section describes some of the internal runtime implementation details. 
Reading this section is not required to be able to use Orleans, but if you are interested to gain a deeper understanding of how certain parts of Orleans are implemented, you will find this section useful.

## Community and Contributions

* **[Orleans Community - Repository of community add-ons to Orleans](https://github.com/OrleansContrib)** 
Various community projects, including Orleans Monitoring, Design Patterns, Storage Provider, etc.

* **[Orleans Community - Virtual Meetups](https://github.com/OrleansContrib/meetups)**

* **[Orleans Community - Gitter Chat Forum](https://gitter.im/dotnet/orleans)**

* **[Orleans Community - Twitter](https://twitter.com/projectorleans)**

* **[Orleans on Codeplex](https://orleans.codeplex.com)** - 
[Discussion forum and questions](https://orleans.codeplex.com/discussions)

* **[Orleans on StackOverflow](http://stackoverflow.com/questions/tagged/orleans)** - 
More questions about Orleans. We prefer that people ask questions on [GitHub](https://github.com/dotnet/orleans/issues) or [Codeplex](https://orleans.codeplex.com/discussions) but will answer stackoverflow questions as well.

* **[Who Is Using Orleans?](Who-Is-Using-Orleans)** 
A partial list of companies, projects and applications which are using Orleans.

### Contributing To This Project

* Notes and guidelines for people wanting to [contribute code changes to Project Orleans](Contributing).

* You are also encouraged to report bugs or start a technical discussion by filing an new [thread](https://github.com/dotnet/orleans/issues) on GitHub.

## Other Documentation Resources

* **[Microsoft Research Project Orleans Home Page](http://research.microsoft.com/projects/orleans/)**
Where the journey began!

* **[Orleans Technical Report - Distributed Virtual Actors for Programmability and Scalability](http://research.microsoft.com/apps/pubs/default.aspx?id=210931)**

* **[Orleans Best Practices](http://research.microsoft.com/apps/pubs/default.aspx?id=244727)** A collection of tips and trick to help design, build, and run an Orleans-based application.

* **[Links](Links)** 
Blog posts, articles and other links about Orleans.

* **[Presentations](Presentations)** 
Presentations about Orleans
