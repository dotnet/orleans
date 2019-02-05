Announcing Orleans 2.1
======================

[Reuben Bond](https://github.com/ReubenBond)
10/1/2018 7:17:59 PM

* * * * *

Today, we announced Orleans 2.1.
This release includes significant performance improvements over 2.0, a major refresh of distributed transaction support, a new code generator, and new functionality for co-hosting scenarios as well as smaller fixes & improvements.
Read the [release notes here](https://github.com/dotnet/orleans/releases/tag/v2.1.0).

New Scheduler
-------------

Starting with Orleans 2.1, we have a [rewritten core scheduler](https://github.com/dotnet/orleans/pull/3792) which takes advantage of [performance improvements to .NET Core's ThreadPool](https://blogs.msdn.microsoft.com/dotnet/2017/06/07/performance-improvements-in-net-core/).
The new scheduler uses work stealing queues with local queue affinity to reduce contention and improve throughput and latency.
These improvements are available on all platforms/frameworks.
Community members have reported significant responsiveness and throughput improvements in their services and our testing indicates up to 30% higher throughput.
This new scheduler also exhibits much lower CPU usage during low-load periods, benefiting co-hosting scenarios and improving the CPU profiling experience.
Special thanks to [Dmytro Vakulenko](https://twitter.com/dVakulen) from the community for driving this work from theory through to experimentation & completion.

Distributed Transactions
------------------------

Orleans originated from Microsoft Research and we continue to partner with MSR and product teams to bring features such as scalable distributed transactions into production.
Distributed transactions support was first introduced as an experimental feature in Orleans 2.0, and in 2.1 we are refreshing the release with a new, fully decentralized, transaction manager.
Aside from the improvements to the transaction manager, the entire transactions system has continued to receive heavy investment in order to ready the code for production and a stable release in a future version of Orleans. We consider distributed transactions to be in "release candidate" quality in 2.1.
Learn about distributed transactions in Orleans from Sergey's talk, [Distributed Transactions are dead, long live distributed transactions! from J On the Beach 2018](https://www.youtube.com/watch?v=8A5bRdyZXJw).
Read the research papers on transactions, actor-oriented database systems, and other topics from the [Orleans Microsoft Research site](https://www.microsoft.com/en-us/research/project/orleans-virtual-actors/#!publications).

Direct Client
-------------

Orleans 2.1 introduces a new way to interact with grains and interoperate with frameworks like ASP.NET or gRPC.
The feature is called *direct client* and it allows co-hosting a client and silo in a way that let the client communicate more efficiently with not just the silo it's attached to, but the entire cluster.
Once direct client is enabled, `IClusterClient` and `IGrainFactory` can be resolved from the silo container and used to create grain references which can be called.
These calls use the local silo's knowledge of the cluster and grain placement to avoid unnecessary copying, serialization, and networking hops.
In addition, because this feature shares the same internals as the silo itself, it provides a seamless experience when it comes to passing grain references between threads.
In Orleans 2.1 we have made direct client an opt-in feature.
Enable it by calling `ISiloHostBuilder.EnableDirectClient()` during silo configuration.

New Code Generator
------------------

This release includes a new code generation package, `Microsoft.Orleans.CodeGenerator.MSBuild`, an alternative to the existing package, `Microsoft.Orleans.OrleansCodeGenerator.Build`.
The new code generator leverages Roslyn for code analysis to avoid loading application binaries.
As a result, it avoids issues caused by clashing dependency versions and differing target frameworks.
If you experience issues, please let us know by opening an issue on [GitHub](https://github.com/dotnet/orleans/).
The new code generator also improves support for incremental builds, which should result in shorter build times.

Other Improvements
------------------

-   [Grain methods can return
    `ValueTask<T>`](https://github.com/dotnet/orleans/pull/4562) -
    thanks to [@kutensky](https://twitter.com/kutensky)
-   [Removed per-call Timer
    allocation](https://github.com/dotnet/orleans/pull/4399), reducing
    .NET Timer queue contention
-   [Fixes](https://github.com/dotnet/orleans/pull/4853)
    [for](https://github.com/dotnet/orleans/pull/4883) silo shutdown
    behavior - thank you to [@yevhen](https://twitter.com/yevhen) for
    reporting and
    [investigating](https://github.com/dotnet/orleans/issues/4757)
-   [Configure grain
    collection](https://github.com/dotnet/orleans/pull/4890) idle time
    using `[CollectionAgeLimit(Minutes = x)]` - thanks to
    [@aRajeshKumar](https://github.com/arajeshkumar)

Known Issues with .NET Core 2.1
-------------------------------

User [Sun Zhongfeng reported an issue](https://github.com/dotnet/orleans/issues/4990) running Orleans on .NET Core 2.1 with TieredCompilation enabled.
We tracked this down to a [JIT issue in CoreCLR](https://github.com/dotnet/coreclr/issues/20040), which the CoreCLR team quickly diagnosed and fixed.
TieredCompilation is not enabled by default in .NET Core 2.1 and this fix is [not expected to land in .NET Core 2.1, but will be included in .NET Core 2.2](https://github.com/dotnet/coreclr/pull/20083#issuecomment-424464934).
**Do not enable TieredCompilation if you are running Orleans on .NET Core 2.1.** We would like to thank everyone in the community who contributed to this release and helped testing the pre-release builds.
