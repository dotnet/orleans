<p align="center">
  <img src="https://github.com/dotnet/orleans/blob/gh-pages/assets/logo_full.png" alt="Orleans logo" width="600px"> 
</p>

[![Build status](https://ci.dot.net/job/dotnet_orleans/job/master/job/bvt/badge/icon)](http://ci.dot.net/job/dotnet_orleans/job/master/)
[![NuGet](https://img.shields.io/nuget/v/Microsoft.Orleans.Core.svg?style=flat)](http://www.nuget.org/profiles/Orleans)
[![Gitter](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/dotnet/orleans?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge)
[![Help Wanted Issues](https://badge.waffle.io/dotnet/orleans.svg?label=up-for-grabs&title=Help%20Wanted%20Issues)](http://waffle.io/dotnet/orleans)

Orleans is a framework that provides a straight-forward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. 

It was created by [Microsoft Research](http://research.microsoft.com/projects/orleans/) 
implementing the [Virtual Actor Model](http://research.microsoft.com/apps/pubs/default.aspx?id=210931) 
and designed for use in the cloud. 

Orleans has been used extensively running in Microsoft Azure by several Microsoft product groups, most notably by [343 Industries](https://www.halowaypoint.com/) as a platform for all of Halo 4 and Halo 5 cloud services, as well as by [a number of other projects and companies](http://dotnet.github.io/orleans/Community/Who-Is-Using-Orleans.html).

Installation
============

Installation is performed via [NuGet](https://www.nuget.org/packages?q=orleans). 
There are several packages, one for each different project type (interfaces, grains, silo, and client).

In the grain interfaces project:
```
PM> Install-Package Microsoft.Orleans.Core.Abstractions
PM> Install-Package Microsoft.Orleans.CodeGenerator.MSBuild
```
In the grain implementations project:
```
PM> Install-Package Microsoft.Orleans.Core.Abstractions
PM> Install-Package Microsoft.Orleans.CodeGenerator.MSBuild
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

The latest clean development branch build from CI is located: [here](https://ci.dot.net/job/dotnet_orleans/job/master/job/bvt/lastStableBuild/artifact/)

Nightly builds are published to https://dotnet.myget.org/gallery/orleans-ci . These builds pass all functional tests, but are not thoroughly tested as the stable builds or pre-release builds we push to NuGet.org

To use nightly builds in your project, add the MyGet feed using either of the following methods:

1. Changing the .csproj file to include this section:

```xml
    <RestoreSources>
      $(RestoreSources);
      https://dotnet.myget.org/F/orleans-ci/api/v3/index.json;
    </RestoreSources>
```
or

2. Creating a `NuGet.config` file in the solution directory with the following contents:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
 <packageSources>
    <clear />
    <add key="orleans-ci" value="https://dotnet.myget.org/F/orleans-ci/api/v3/index.json" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
 </packageSources>
</configuration>
```

### Building from source

Clone the sources from the GitHub [repo](https://github.com/dotnet/orleans) 

Run the `Build.cmd` script to build the nuget packages locally,
then reference the required NuGet packages from `/Artifacts/Release/*`.
You can run `Test.cmd` to run all BVT tests, and `TestAll.cmd` to also run Functional tests (which take much longer)

### Building and running tests in Visual Studio 2017
.NET Core 2.0 SDK is a pre-requisite to build Orleans.sln.

There might be errors trying to build from Visual Studio because of conflicts with the test discovery engine (error says could not copy `xunit.abstractions.dll`).
The reason for that error is that you need to configure the test runner in VS like so (after opening the solution):
* `Test` -> `Test Settings` -> Uncheck `Keep Test Execution Engine running`
* `Test` -> `Test Settings` -> `Default Processor Architecture` -> Check `X64`

Then either restart VS, or go to the task manager and kill the processes that starts with `vstest.`. Then build once again and it should succeed and tests should appear in the `Test Explorer` window.

Documentation
=============

Documentation is located [here](https://dotnet.github.io/orleans/Documentation/)

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
  public Task<string> SayHello(string greeting)
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

Blog
=========
[Orleans Blog](https://dotnet.github.io/orleans/blog/) is a place to share our thoughts, plans, learnings, tips and tricks, and ideas, crazy and otherwise, which don’t easily fit the documentation format. We would also like to see here posts from the community members, sharing their experiences, ideas, and wisdom. 
So, welcome to Orleans Blog, both as a reader and as a blogger!

Community
=========

* Ask questions by [opening an issue on GitHub](https://github.com/dotnet/orleans/issues) or on [Stack Overflow](https://stackoverflow.com/questions/ask?tags=orleans)

* [Chat on Gitter](https://gitter.im/dotnet/orleans)

* Follow the [@MSFTOrleans](https://twitter.com/MSFTOrleans) Twitter account for Orleans announcements.

* [OrleansContrib - Repository of community add-ons to Orleans](https://github.com/OrleansContrib/) Various community projects, including Orleans Monitoring, Design Patterns, Storage Provider, etc.

* Guidelines for developers wanting to [contribute code changes to Orleans](http://dotnet.github.io/orleans/Community/Contributing.html).

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
* [Contributing](http://dotnet.github.io/orleans/Community/Contributing.html)

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
