---
layout: page
title: What's new in Orleans
---
{% include JB/setup %}

## [v1.2.3](https://github.com/dotnet/orleans/releases/tag/v1.2.3) July 11th 2016

### Release notes

- Ability to force creation of Orleans serializers for types not marked with [Serializable] by using GenerateSerializer, KnownType or KnownAssembly.TreatTypesAsSerializable [#1888](https://github.com/dotnet/orleans/pull/1888) [#1864](https://github.com/dotnet/orleans/pull/1864) [#1855](https://github.com/dotnet/orleans/pull/1855)
- Troubleshooting improvements:
  - Fixed stacktrace preservation in exceptions from grain calls (bug introduced in 1.2.0) [#1879](https://github.com/dotnet/orleans/pull/1879) [#1808](https://github.com/dotnet/orleans/pull/1808)
  - Better messaging when silo fails to join due to initial connectivity problems [#1866](https://github.com/dotnet/orleans/pull/1866) [#1933](https://github.com/dotnet/orleans/pull/1933)
  - Throw meaningful exception if grain timer is created outside grain context [#1858](https://github.com/dotnet/orleans/pull/1858)
- Bug fixes:
  - Do not deactivate Stateless Workers upon grain directory partition shutdown [#1838](https://github.com/dotnet/orleans/pull/1838)
  - interception works with Streams and grain extensions [#1874](https://github.com/dotnet/orleans/pull/1874)
  - Memory Storage provider properly enforces etags for any state that has been added or removed, but does not enforce etags for newly added state. [#1885](https://github.com/dotnet/orleans/pull/1885)
  - Other minor bug fixes [#1884](https://github.com/dotnet/orleans/pull/1884) [#1823](https://github.com/dotnet/orleans/pull/1823)

## [v1.2.2](https://github.com/dotnet/orleans/releases/tag/v1.2.2) June 15th 2016

### Release notes

* Bugfix: Remote stacktrace is once again being included in the exception that bubbles up to the caller (bug introduced in 1.2.0). [#1808](https://github.com/dotnet/orleans/pull/1808)
* Bugfix: Memory Storage provider no longer throws NullReferenceException after the grain state is cleared. [#1804](https://github.com/dotnet/orleans/pull/1804)
* Microsoft.Orleans.OrleansCodeGenerator.Build package updated to not add the empty orleans.codegen.cs content file at install time, and instead create it at build time (should be more compatible with NuGet Transitive Restore). [#1720](https://github.com/dotnet/orleans/pull/1720)
* Added GrainCreator abstraction to enable some unit testing scenarios. [#1802](https://github.com/dotnet/orleans/pull/1802/) & [#1792](https://github.com/dotnet/orleans/pull/1792/)
* ServiceBus package dependency upgraded to 3.2.2 [#1758](https://github.com/dotnet/orleans/pull/1758/)

## [v1.2.1](https://github.com/dotnet/orleans/releases/tag/v1.2.1) May 19th 2016

### Release notes

* SupressDuplicateDeads: Use SiloAddress.Endpoint instead of InstanceName. [1728](https://github.com/dotnet/orleans/pull/1728)
* Added support for complex generic grain parameters. [#1732](https://github.com/dotnet/orleans/pull/1732)
* Fix race condition bugs in LocalReminderService. [#1757](https://github.com/dotnet/orleans/pull/1757)

## [v1.2.0](https://github.com/dotnet/orleans/releases/tag/v1.2.0) May 4th 2016

### Release notes

In addition to all the changes in 1.2.0-beta.

* [Azure storage 7.0 compatibility](https://github.com/dotnet/orleans/pull/1704).
* Updated to latest version of Consul and ZooKeeper NuGets.
* [Added ability to throw exceptions that occur when starting silo](https://github.com/dotnet/orleans/pull/1711).

## [v1.2.0-beta](https://github.com/dotnet/orleans/releases/tag/v1.2.0-beta) April 18th 2016

### Release notes

* Major improvements
  * Added an EventHub stream provider based on the same code that is used in Halo 5.
  * [Increased throughput by between 5% and 26% depending on the scenario.](https://github.com/dotnet/orleans/pull/1586)
  * Migrated all but 30 functional tests to GitHub.
  * Grain state doesn't have to extend `GrainState` anymore (marked as `[Obsolete]`) and can be a simple POCO class. 
  * [Added support for per-grain-class](https://github.com/dotnet/orleans/pull/963) and [global server-side interceptors.](https://github.com/dotnet/orleans/pull/965) 
  * [Added support for using Consul 0.6.0 as a Membership Provider.](https://github.com/dotnet/orleans/pull/1267)
  * [Support C# 6.](https://github.com/dotnet/orleans/pull/1479)
  * [Switched to xUnit for testing as a step towards CoreCLR compatibility.](https://github.com/dotnet/orleans/pull/1455)

* Codegen & serialization
  * [Added support for generic type constraints in codegen.](https://github.com/dotnet/orleans/pull/1137)
  * [Added support for Newtonsoft.Json as a fallback serializer.](https://github.com/dotnet/orleans/pull/1047)
  * [Added generation of serializers for type arguments of `IAsyncObserver<T>`.](https://github.com/dotnet/orleans/pull/1319)
  * [Improved support for F# interfaces.](https://github.com/dotnet/orleans/pull/1369)
  * [Consolidated two compile time codegen NuGet packages into one `Microsoft.Orleans.OrleansCodeGenerator.Build`. `Microsoft.Orleans.Templates.Interfaces` and `Microsoft.Orleans.Templates.Grains` are now meta-packages for backward compatibility only.](https://github.com/dotnet/orleans/pull/1501)
  * [Moved to Newtonsoft.Json 7.0.1.](https://github.com/dotnet/orleans/pull/1302)

* Programmatic config
  * [Added helper methods for programmatic test configuration.](https://github.com/dotnet/orleans/pull/1411)
  * [Added helper methods to `AzureClient` and `AzureSilo` for easier programmatic config.](https://github.com/dotnet/orleans/pull/1622)
  * [Added extension methods for using programmatic config.](https://github.com/dotnet/orleans/pull/1623)
  * [Remove config filed from Server and Client NuGet packages.](https://github.com/dotnet/orleans/pull/1629)

* Other
  * [Improved support for SQL membership, reminders, and grain storage.](https://github.com/dotnet/orleans/pull/1060)
  * [Improved propagation of exception, so that the caller gets the originally thrown exception instead of an `AggregateException` wrapping it.](https://github.com/dotnet/orleans/pull/1356)
  * [Added a storage provider for Azure Blob (graduated from `OrleansContrib`).](https://github.com/dotnet/orleans/pull/1376)
  * [Start Reminder Service initial load in the background.](https://github.com/dotnet/orleans/pull/1520)
  * [Added automatic cleanup of dead client stream producers and consumers](https://github.com/dotnet/orleans/pull/1429) and [this.](https://github.com/dotnet/orleans/pull/1669)
  * [Added GetPrimaryKeyString extension method for `IAddressable`.](https://github.com/dotnet/orleans/pull/1675)
  * [Added support for additional application directories.](https://github.com/dotnet/orleans/pull/1674)

* Many other fixes and improvements.

## [v1.1.3](https://github.com/dotnet/orleans/releases/tag/v1.1.3) March 9th 2016

### Release notes

A patch release with a set of bug fixes.

* [Initialize SerializationManager before CodeGeneratorManager](https://github.com/dotnet/orleans/pull/1345)
* [Avoid unnecessary table scan when finding reminder entries to delete](https://github.com/dotnet/orleans/pull/1348)
* [Stop a stuck BlockingCollection.Take operation that caused thread leak on the client.](https://github.com/dotnet/orleans/pull/1351)
* [Fixed Azure table property being not sanitized.](https://github.com/dotnet/orleans/pull/1381)
* [Fixed String.Format arguments in DetailedGrainReport.ToString()](https://github.com/dotnet/orleans/pull/1384)
* [Increment and DecrementMetric methods in Orleans.TraceLogger had same body](https://github.com/dotnet/orleans/pull/1405)
* [Update the custom serializer warning message to adequately reflect the OSS status of Orleans](https://github.com/dotnet/orleans/pull/1414)
* [Fix retry timeout when running under debugger](https://github.com/dotnet/orleans/pull/1503)
* [Networking bug fix: Reset receive buffer on error.](https://github.com/dotnet/orleans/pull/1478)
* [Fixed performance regression in networking](https://github.com/dotnet/orleans/pull/1518)
* [Start ReminderService initial load in the background](https://github.com/dotnet/orleans/pull/1520)
* [Safe load of types from failing assemblies in TypeUtils.GetTypes](https://github.com/dotnet/orleans/pull/1534)


## Community Virtual Meetup #9
[Nehme Bilal](https://github.com/nehmebilal) and [Reuben Bond](https://github.com/ReubenBond) [talk about deploying Orleans](https://youtu.be/w__D7gnqeZ0) with [YAMS](https://github.com/Microsoft/Yams) and [Service Fabric](https://azure.microsoft.com/en-gb/documentation/articles/service-fabric-overview/) 
Fabruary 26st 2016

## Community Virtual Meetup #8.5
[Networking discussion](https://youtu.be/F1Yoe88HEvg) hosted by [Jason Bragg](https://github.com/jason-bragg)
February 11th 2016

## Community Virtual Meetup #8
[Orleans core team present the roadmap](https://www.youtube.com/watch?v=4BiCyhvSOs4) 
January 21st 2016


## [v1.1.2](https://github.com/dotnet/orleans/releases/tag/v1.1.2) January 20th 2016

### Release notes

A patch release with bug fixes, primarily for codegen and serializer corner cases.

* [Add support for generic type constraints in codegen](https://github.com/dotnet/orleans/pull/1137)
* [Correctly specify struct type constraint in generated code](https://github.com/dotnet/orleans/pull/1178)
* [fix issue:GetReminder throws exception when reminder don't exists #1167](https://github.com/dotnet/orleans/pull/1182)
* [Cleanup/fix usage of IsNested vs. IsNestedXXX & serialize nested types.](https://github.com/dotnet/orleans/pull/1240)
* [Correctly serialize [Obsolete] fields and properties.](https://github.com/dotnet/orleans/pull/1241)
* [Nested serialization of Guid with Json serializer.](https://github.com/dotnet/orleans/pull/1249)
* [Fix a race in StreamConsumer.SubscribeAsync.](https://github.com/dotnet/orleans/pull/1261)
* [fix deepcopy issue #1278](https://github.com/dotnet/orleans/pull/1280)
* [Check declaring types when performing accessibility checks for code gen.](https://github.com/dotnet/orleans/pull/1284)
* [Allow to configure PubSub for SMS.](https://github.com/dotnet/orleans/pull/1285)
* [Make Namespace access modifier public in ImplicitStreamSubscriptionAttribute. Add Provider property.](https://github.com/dotnet/orleans/pull/1270)


## [v1.1.1](https://github.com/dotnet/orleans/releases/tag/v1.1.1) January 11th 2016

### Release notes

A patch release for two bug fixes

* [Missing argument to trace format in TraceLogger.Initialize](https://github.com/dotnet/orleans/pull/1134)
* [Make ConsoleText resilient to ObjectDisposedExceptions](https://github.com/dotnet/orleans/pull/1195)

## [Community Virtual Meetup #7](https://www.youtube.com/watch?v=FKL-PS8Q9ac)
Christmas Special - [Yevhen Bobrov](https://github.com/yevhen) on [Orleankka](https://github.com/yevhen/Orleankka)
December 17th 2015

## [v1.1.0](https://github.com/dotnet/orleans/releases/tag/v1.1.0) December 14nd 2015

### Release notes

* New Roslyn-based codegen, compile time and run time
* Public APIs:
  * Core API for Event Sourcing
  * Most methods of `Grain` class are now virtual
  * ASP.NET vNext style Dependency Injection for grains
  * New telemetry API
* Portability:
  * Support for C# 6.0
  * Improved support for F# and VB
  * Code adjustments towards CoreCLR compliance
  * Orleans assemblies are not strong-named anymore
* SQL:
  * `OrleansSQLUtils.dll` for SQL-related functionality
  * MySQL is now supported as a cluster membership store
  * Storage provider for SQL Server
* Serialization:
  * Support for pluggable external serializers
  * Bond serializer plugin
  * Support for Json.Net as a fallback serializer
  * Added `[KnownType]` attribute for generating serializers for arbitrary types
* Upgraded to Azure Storage 5.0
* Upgraded to .NET 4.5.1
* Other fixes and improvements

## Community Virtual Meetup #6
[MSR PhDs on Geo Distributed Orleansp](https://www.youtube.com/watch?v=fOl8ophHtug) 
October 23rd 2015

## [v1.0.10](https://github.com/dotnet/orleans/releases/tag/v1.0.10) September 22nd 2015

### Release notes

#### General:
* No SDK msi anymore, only NuGets from now on
* Removed support for grain state interfaces and code generation of state classes
* Removed code generated  MyGrainFactory.GetGrain()  factory methods
* StorageProvider  attribute is now optional
* Membership and reminder table implementations were made pluggable
* Improvements to  ObserverSubscriptionManager  
* Strong guarantee for specified max number of  StatelessWorker  activations per silo
* General purpose interface for sending run time control commands to providers
* Named event to trigger silo shutdown

#### Streaming:
* Support for multiple  ImplicitSubscription  attributes for streams
* Support for rewinding of implicit stream subscriptions
* Propagate request context via persistent streams
* More options for stream Queue Balancers
* Delayed repartitioning of stream queues
* Improved cleanup of client stream producers/consumers when client shuts down
* Config option and management grain API for controlling start/stop state of stream pulling agents
* Azure Queue stream provider fixed to guarantees at least once delivery
* Numerous bug fixes and improvements, mostly to streaming


## [v1.0.9](https://github.com/dotnet/orleans/releases/tag/v1.0.9) July 15th  2015

### This release includes several significant API changes that require adjustments in existing code created with v1.0.8 or earlier versions of Orleans  ###

**1. `GrainFactory` is not a static class anymore, so that the default implementation can be substituted for testing and other reasons.**

Within grain code one can still use code like `GrainFactory.GetGrain<IFoo>(grainKey)` because `Grain` class now has a `GrainFactory` property, which makes the above code translate to `base.GrainFactory.GetGrain<IFoo>(grainKey)`. So no change is necessary for such code.

Within the client (frontend) context the default `GrainFactory` is available as a property on the `GrainClient` class. So, the code that used to be `GrainFactory.GetGrain<IFoo>(grainKey)` needs to be changed to `GrainClient.GrainFactory.GetGrain<IFoo>(grainKey)`

**2. `Read/Write/ClearStateAsync` methods methods that on grain state were moved from state objects to `Grain` class**

Wherever you have grain code like `this.State.WriteStateAsync()`, it needs to change to `this.WriteStateAsync()`. Similar adjustments need to be made to usage of `ReadeStateAsync()` and `CleareStateAsync()`.

**3. Binaries have been removed from the Orleans SDK**

If your projects still reference those binaries directly from the SDK folder, you need to switch to using [NuGet packages](Nugets) instead. If you are already consuming Orleans via NuGet, you are good.

**4. Local Silo test environment has been removed from the Orleans SDK**

If you were using the Local Silo environment from the SDK folder for testing your grains, you need to add a silo host project to your solution using the "Orleans Dev/Test Host" Visual Studio project template. Before you do that, make sure you install the [v1.0.9 version of the SDK](https://github.com/dotnet/orleans/releases/download/v1.0.9/orleans_setup.msi). Refer to [samples](https://github.com/dotnet/orleans/tree/master/Samples) for examples of how that is done.

### Release notes

* Graceful shutdown of a silo with deactivation of all grains hosted in it.
* Support for Dependency Injection and better testability of grains: 
	* Direct instantiation of grains with passing `IGrainIdentity` and `IGrainRuntime` to constructor.
	* `IGrainRuntime` is a mockable interface that includes a set of system service interfaces, also mockable.
	* `GrainFactory` is a non-static class that is accessed via `base.GrainFactory`  from within a grain and via  `GrainClient.GrainFactory`  on the client.
* Deprecated generated per-interface `GetGrain()` static factory methods.
* Added support for concrete grain state classes, deprecated grain state interfaces and code generation of grain classes.
* Removed `Read/Write/ClearStateAsync` methods from `IGrainState` and moved them to `Grain<T>`.
* Performance optimizations of messaging with up to 40% improvements in throughput.
* Added ZooKeeper based cluster membership storage option.
* Removed compile time dependency on Microsoft.WindowsAzure.ServiceRuntime.dll.
* Consolidated dependencies on Azure in OrleansAzureUtils.dll, which is now optional.
* Refactored SQL system store to be more robust and vendor agnostic.
* Added streaming event deliver policy and failure reporting.
* Changed VS project templates to use only NuGet packages and not the SDK.
* Removed binaries and local silo environment from the SDK.
* Numerous bug fixes and other improvements.

## [v1.0.8](https://github.com/dotnet/orleans/releases/tag/v1.0.8) May 26th  2015

### Release notes

* Fixed versions of references Orleans NuGet packages to match the current one.
* Switched message header keys from strings to enums for performance.
* Fixed a deadlock issue in deactivation process.
* Added a NuGet package to simplify testing of grain projects - Microsoft.Orleans.TestingHost.
* Fixed regression of reporting codegen error to Visual Studio Errors window.
* Added version to SDK msi product and folder name.
* Other fixes and improvements.


## [Community Virtual Meetup #5](https://www.youtube.com/watch?v=eSepBlfY554)
[Gabriel Kliot](https://github.com/gabikliot) on the new Orleans Streaming API
May 22nd 2015


## [v1.0.7](https://github.com/dotnet/orleans/releases/tag/v1.0.7) May 15th  2015

### Release notes

* Major refactoring of the stream adapter API.
* Improvements to the streaming API to support subscription multiplicity.
* Made IAddressable.AsReference strongly-typed.
* Added a Chocolatey package.
* Added support for private storage keys for testing.
* Replaced ExtendedPrimaryKeyAttribute with IGrainWithGuidCompoundKey and IGrainWithIntegerCompoundKey.
* Added support for grain classes that are implementations of generic grain interfaces with concrete type arguments.
* Numerous other fixes and improvements.


## [Community Virtual Meetup #4](https://www.youtube.com/watch?v=56Xz68lTB9c)
[Reuben Bond](https://github.com/ReubenBond) on using Orleans at FreeBay
April 15th 2015


## [v1.0.5](https://github.com/dotnet/orleans/releases/tag/v1.0.5) March 30th  2015

### Release notes

* Major reorganization of NuGet packages and project templates.
* Fixes to reflection-only assembly inspection and loading for side-by-side versioning.
* Improved scalability of observers.
* Programmatic configuration of providers.
* Numerous other fixes and improvements.


## [Community Virtual Meetup #3](https://www.youtube.com/watch?v=07Up88bpl20)
[Yevhen Bobrov](https://github.com/yevhen) on a Uniform API for Orleans
March 6th 2015

## [Community Virtual Meetup #2](https://www.youtube.com/watch?v=D4kJKSFfNjI)
Orleans team live Q&A and roadmap
January 12th 2015


## Orleans Open Source v1.0 Update (January 2015)

###Initial stable production-quality release.

Since the September 2014 Preview Update we have made a small number of public API changes, mainly related to clean up and more consistent naming. Those changes are summarized below:

### Public Type Names Changes

Old API   | New API
------------- | -------------
OrleansLogger | Logger
OrleansClient | GrainClient 
Grain.ActivateAsync | Grain.OnActivateAsync
Grain.DeactivateAsync | Grain.OnDeactivateAsync
Orleans.Host.OrleansSiloHost | Orleans.Runtime.Host.SiloHost 
Orleans.Host.OrleansAzureSilo | Orleans.Runtime.Host.AzureSilo
Orleans.Host.OrleansAzureClient| Orleans.Runtime.Host.zureClient
Orleans.Providers.IOrleansProvider | Orleans.Providers.IProvider
Orleans.Runtime.ActorRuntimeException | Orleans.Runtime.OrleansException
OrleansConfiguration | ClusterConfiguration
LoadAwarePlacementAttribute | ActivationCountBasedPlacementAttribute

### Other Changes

* All grain placement attribute (including [`StatelessWorker`]) now need to be defined on grain implementation class, rather than on grain interface.
* `LocalPlacementAttribute` was removed. There are now only `StatelessWorker` and `PreferLocalPlacement`.
* Support for Reactive programming with Async RX. 
* Orleans NuGet packages are now published on NuGet.org. 
  See this wiki page for advice on how to [convert legacy Orleans grain interface / class projects over to using NuGet packages](Convert-Orleans-v0.9-csproj-to-Use-v1.0-NuGet).


## [Community Virtual Meetup #1](http://www.youtube.com/watch?v=6COQ8XzloPg)
[Jakub Konecki](https://github.com/jkonecki) on Event Sourced Grains
December 18th 2014

