Orleans - Distributed Actor Model
=======

![Orleans logo](https://github.com/dotnet/orleans/blob/gh-pages/Icons/Orleans/OrleansSDK_128x.png)


[![Build status](http://dotnet-ci.cloudapp.net/job/dotnet_orleans/job/innerloop/badge/icon)](http://dotnet-ci.cloudapp.net/job/dotnet_orleans/job/innerloop)
[![NuGet](https://img.shields.io/nuget/v/Microsoft.Orleans.Core.svg?style=flat)](http://www.nuget.org/profiles/Orleans)
[![Issue Stats](http://www.issuestats.com/github/dotnet/orleans/badge/pr)](http://www.issuestats.com/github/dotnet/orleans)
[![Issue Stats](http://www.issuestats.com/github/dotnet/orleans/badge/issue)](http://www.issuestats.com/github/dotnet/orleans)

[![Gitter](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/dotnet/orleans?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge)

[![Help Wanted Issues](https://badge.waffle.io/dotnet/orleans.svg?label=up-for-grabs&title=Help Wanted Issues)](http://waffle.io/dotnet/orleans)

Orleans is a framework that provides a straight-forward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. 

It was created by [Microsoft Research](http://research.microsoft.com/projects/orleans/) 
implementing the [Virtual Actor Model](http://research.microsoft.com/apps/pubs/default.aspx?id=210931) 
and designed for use in the cloud. 

Orleans has been used extensively running in Microsoft Azure by several Microsoft product groups, most notably by [343 Industries](https://www.halowaypoint.com/) as a platform for all of Halo 4 and Halo 5 cloud services, as well as by [a number of other projects and companies](http://dotnet.github.io/orleans/Who-Is-Using-Orleans).

Installation
============

Installation is performed via [NuGet](https://www.nuget.org/packages?q=orleans). 
There are several packages, one for each different project type (interfaces, grains, silo, and client).

In the grain interfaces project:
```
PM> Install-Package Microsoft.Orleans.Templates.Interfaces
```
In the grain implementations project:
```
PM> Install-Package Microsoft.Orleans.Templates.Grains
```
In the server (silo) project:
```
PM> Install-Package Microsoft.Orleans.Server
```
In the client project:
```
PM> Install-Package Microsoft.Orleans.Client
```

### Official Builds

The stable production-quality release is located [here](https://github.com/dotnet/orleans/releases/latest).

The latest clean development branch build from CI is located: [here](http://dotnet-ci.cloudapp.net/job/dotnet_orleans/job/innerloop/lastStableBuild/artifact/)

### Building From Source

Clone the sources from the GitHub [repo](https://github.com/dotnet/orleans) 

Run run the `Build.cmd` script to build the binaries locally,
then reference the required NuGet packages from `Binaries\NuGet.Packages\*`.

Documentation
=============

Documentation is located [here](http://dotnet.github.io/orleans/)

Code Examples
=============

Create an interface for your grain:
```c#
public interface IHello : Orleans.IGrainWithIntegerKey
{
  Task<string> SayHello(string greeting);
}
```

Provide an implementation of that interface:
```c#
public class HelloGrain : Orleans.Grain, IHello
{
  Task<string> SayHello(string greeting)
  {
    return Task.FromResult($"You said: '{greeting}', I say: Hello!");
  }
}
```

Call the grain from your Web service (or anywhere else):
```c#
// Get a reference to the IHello grain with id '0'.
var friend = GrainClient.GrainFactory.GetGrain<IHello>(0);

// Send a greeting to the grain and await the response.
Console.WriteLine(await friend.SayHello("Good morning, my friend!"));
```

Community
=========

* Ask questions by [opening an issue on GitHub](https://github.com/dotnet/orleans/issues) or on [Stack Overflow](https://stackoverflow.com/questions/ask?tags=orleans)

* [Chat on Gitter](https://gitter.im/dotnet/orleans)

* Follow the [@ProjectOrleans](https://twitter.com/ProjectOrleans) Twitter account for Orleans project news announcements.

* [OrleansContrib - Repository of community add-ons to Orleans](https://github.com/OrleansContrib/) Various community projects, including Orleans Monitoring, Design Patterns, Storage Provider, etc.

* Guidelines for developers wanting to [contribute code changes to Project Orleans](http://dotnet.github.io/orleans/Contributing).

* You are also encouraged to report bugs or start a technical discussion by starting a new [thread](https://github.com/dotnet/orleans/issues) on GitHub.

License
=======
This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/master/LICENSE).

Quick Links
===========

* [MSR-ProjectOrleans](http://research.microsoft.com/projects/orleans/)
* Orleans Tech Report - [Distributed Virtual Actors for Programmability and Scalability](http://research.microsoft.com/apps/pubs/default.aspx?id=210931)
* [Orleans-GitHub](https://github.com/dotnet/orleans)
* [Orleans Documentation](http://dotnet.github.io/orleans/)
* [Contributing](http://dotnet.github.io/orleans/Contributing)
