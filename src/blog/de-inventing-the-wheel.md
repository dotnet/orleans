De-inventing the wheel
======================

[Julian Dominguez](https://github.com/jdom)
4/12/2017 2:49:11 PM

* * * * *

The Microsoft Orleans project started many years ago in Microsoft Research, when not even the Task class existed.
As the project matured, many non-core abstractions & functionality was needed to support its growth.
These abstractions didn't exist as standards in .NET and .NET OSS was in its infancy.
Examples of these pieces are cross-cutting concerns such as Logging, Configuration, and Dependency Injection.

As time passed, some common abstractions and patterns emerged, and it reached a point where it makes sense to just adopt them.
There are many reasons as to why:

- Developers are used to the standard patterns and abstractions, so newcomers do not need to learn these non-core abstractions just to use Orleans.
- Standard abstractions have an enormous level of adoption with almost every 3rd party component related to that abstraction. On the other hand, today it requires that the Orleans community builds integration packages to many 3rd party components (ie: to use Serilog, log4net, or push events to ETW), as the owners of these will just create integration packages for the common abstractions, but not for Orleans or any other non-standard abstraction.
- We created custom abstractions to be good enough to do the job, but we don't focus too much after that on usability, as it just goes into maintenance mode. Sometimes we find out that these abstractions were not good enough, so we must make breaking changes (for example our move to non-static clients and silos requires a non-static logging abstraction).
- These standard abstractions are very well thought out to do that specific job, and are generally very flexible, simple to use, and have a lot of documentation. We just stand on their shoulders.
- Deleting code that is not important to Orleans core functionality is always good.

![Reinventing the wheel leads to unnecessary work](media/2017/04/reinvent-the-wheel.jpg)

We already started using the [Microsoft.Extensions.DependencyInjection](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection) abstractions for enabling DI, moving away for our poor man's object activation (and 2-step initialization) approach in many places.

As we move forward, we plan to deprecate some of our custom abstractions in favor of standard ones.
In particular we are already thinking of 2 upcoming changes:

- Migrate our logging abstractions to
 [Microsoft.Extensions.Logging](https://www.nuget.org/packages/Microsoft.Extensions.Logging).
- Revamp our configuration and startup pattern to align with ASP.NET  Core's. See [dotnet/orleans\#2936](https://github.com/dotnet/orleans/issues/2936) for an initial design of this move.

As always, we'll try to keep breaking changes to a minimum, but we don't strictly prevent breaking changes.
Sometimes we make our new versions be source code compatible (meaning that developers can't simply use binding redirects on Orleans assemblies, but re-building their code might still compile) or require a few minimal fixes.
Sometimes breaking changes are \*bigger\* if they would just affect a small feature or something that is typically not spread-out through the entire codebase of our users (such as extensibility points that do not affect grain code).

Also, it seems like an appropriate time to look at these abstractions with a fresh mind, since that's what the .NET community seems to be doing when looking forward at things like ASP.NET and .NET Core.
