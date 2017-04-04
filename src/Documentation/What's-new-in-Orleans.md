---
layout: page
title: What's new in Orleans
---

# What's new in Orleans?

## [v1.4.1](https://github.com/dotnet/orleans/releases/tag/v1.4.1) March 27th 2017

### Release notes

Improvements and bug fixes since 1.4.0.
Addressed the known issue in 1.4.0: *"When the silo starts up, it will register IServiceProvider in the container, which can be a circular reference registration when using 3rd party containers such as AutoFac."*

- Improvements
  - Fix a cleanup issue in TestCluster after a failure [#2734](https://github.com/dotnet/orleans/pull/2734/)
  - Remove unnecessary service registration of IServiceProvider to itself, which improves support for 3rd party containers [#2749](https://github.com/dotnet/orleans/pull/2749/)
  - Add a timeout for socket connection [#2791](https://github.com/dotnet/orleans/pull/2791/)
  - Support for string grain id in OrleansManager.exe [#2815](https://github.com/dotnet/orleans/pull/2815/)
  - Avoid reconnection to gateway no longer in the list returned by IGatewayListProvider [#2824](https://github.com/dotnet/orleans/pull/2824/)
  - Handle absolute path in IntermediateOutputPath to address issue 2864 [#2865](https://github.com/dotnet/orleans/pull/2865/), [#2871](https://github.com/dotnet/orleans/pull/2871/)
  - Rename codegen file to be excluded from analyzers [#2872](https://github.com/dotnet/orleans/pull/2872/)
  - ProviderTypeLoader: do not enumerate types in ReflectionOnly assembly. [#2869](https://github.com/dotnet/orleans/pull/2869/)
  - Do not throw when calling Stop on AsynchQueueAgent after it was disposed.
- Bug fixes
  - NodeConfiguration.AdditionalAssemblyDirectories was not 'deeply' copied in the copy constructor [#2758](https://github.com/dotnet/orleans/pull/2758/)
  - Fix AsReference() in generated code for null values [#2756](https://github.com/dotnet/orleans/pull/2756/)
  - Avoid a NullReferenceException in SerializationManager.Register(...) [#2768](https://github.com/dotnet/orleans/pull/2768/)
  - Fix missing check for empty deployment id [#2786](https://github.com/dotnet/orleans/pull/2786/)
  - Fix to make OrleansPerfCounterTelemetryConsumer still work for grain-specific counters. (part of [#2807](https://github.com/dotnet/orleans/pull/2807/))
  - Fix typos in format strings [#2853](https://github.com/dotnet/orleans/pull/2853/)
  - Fix null reference exception in simple queue cache. [#2829](https://github.com/dotnet/orleans/pull/2829/) 


## Community Virtual Meetup #14

[Orleans FSM](https://youtu.be/XmsVYLfNHjI) with [John Azariah](https://github.com/johnazariah)
March 22nd 2017


## [v1.4.0](https://github.com/dotnet/orleans/releases/tag/v1.4.0) February 21st 2017

### Release notes

- Major new features
  - Revamped JournaledGrain for event sourcing with support for geo-distributed log-based consistency providers.
  - Abstraction of Grain Services with fixed-placed per-silo application components with their workload partitioned via cluster consistency ring.
  - Support for heterogeneous silos with non-uniform distribution of available grain classes.
  - Cluster membership provider for Service Fabric.

####New in v1.4.0 since beta

- Known issues 
  - When the silo starts up, it will register IServiceProvider in the container, which can be a circular reference registration when using 3rd party containers such as AutoFac. This is bein addressed for 1.4.1, but there is a simple workaround for it at #2747
  - The build-time code generator required (and automatically added) a file named Properties\orleans.codegen.cs to the project where codegen was being ran. The new MSBuild targets no longer do that, so when upgrading a solution with a previous version of Orleans, you should delete this orleans.codegen.cs file from your grain projects.

- Improvements 
  - Support for grains with generic methods [#2670](https://github.com/dotnet/orleans/pull/2670/)
  - Do native PE files check before assembly loading to avoid misleading warnings on startup [#2714](https://github.com/dotnet/orleans/pull/2714/)
  - Throw explicit exception when using streams not in Orleans Context [#2683](https://github.com/dotnet/orleans/pull/2683/)
  - Several improvements to JournaledGrain API [#2651](https://github.com/dotnet/orleans/pull/2651/) [#2720](https://github.com/dotnet/orleans/pull/2720/)
  - Allow overriding MethodId using [MethodId(id)] on interface methods [#2660](https://github.com/dotnet/orleans/pull/2660/)
- Bug fixes 
  - EventHubSequenceToken not used consistently [#2724](https://github.com/dotnet/orleans/pull/2724/)
  - Support grains with generic state where the state param do not match the grain type params [#2715](https://github.com/dotnet/orleans/pull/2715/)
  - Fix ServiceFabric IFabricServiceSiloResolver registration in DI container [#2712](https://github.com/dotnet/orleans/pull/2712/)
  - Fix ConditionalCheckFailedException when updating silo 'IAmAlive' field in DynamoDB [#2678](https://github.com/dotnet/orleans/pull/2678/)
  - Ensure DynamoDB Gateway Provider only returns silos with a proxy port configured [#2679](https://github.com/dotnet/orleans/pull/2679/)
  - Fix e-Tag issue in AzureBlobStorage when calling ClearStateAsync ([#2669](https://github.com/dotnet/orleans/pull/2669/))
  - Other minor fixes: [#2729](https://github.com/dotnet/orleans/pull/2729/) [#2691](https://github.com/dotnet/orleans/pull/2691/)

## Community Virtual Meetup #13

[Upgrading Orleans Applications](https://youtu.be/_5hWNVccKeQ) with [Sergey Bykov](https://github.com/sergeybykov) and team
February 8th 2017
[Presentation](https://github.com/dotnet/orleans/raw/gh-pages/Presentations/VM-13%20-%20Orleans%20%26%20versioning.pptx)

## [v1.4.0-beta](https://github.com/dotnet/orleans/releases/tag/v1.4.0-beta) February 1st 2017

### Release notes

- Major new features
  - Revamped JournaledGrain for event sourcing with support for geo-distributed log-based consistency providers.
  - Abstraction of Grain Services with fixed-placed per-silo application components with their workload partitioned via cluster consistency ring.
  - Support for heterogeneous silos with non-uniform distribution of available grain classes.
  - Cluster membership provider for Service Fabric.

- Improvements
  - IL-based fallback serializer [#2162](https://github.com/dotnet/orleans/pull/2162/)
  - IncomingMessageAcceptor sockets change from APM to EAP [#2275](https://github.com/dotnet/orleans/pull/2275/)
  - Show clearer error when ADO.NET provider fails to init [#2303](https://github.com/dotnet/orleans/pull/2303/), [#2306](https://github.com/dotnet/orleans/pull/2306/)
  - In client, when a gateway connection close reroute not yet sent message to another gateway [#2298](https://github.com/dotnet/orleans/pull/2298/)
  - MySQL Script: Minor syntax tweak to support previous server versions [#2342](https://github.com/dotnet/orleans/pull/2342/)
  - Azure Queue provider message visibility config [#2401](https://github.com/dotnet/orleans/pull/2401/)
  - Propagate exceptions during message body deserialization [#2364](https://github.com/dotnet/orleans/pull/2364/)
  - Check IAddressable before DeepCopy [#2383](https://github.com/dotnet/orleans/pull/2383/)
  - Modified stream types to not use fallback serializer and allow external [#2330](https://github.com/dotnet/orleans/pull/2330/)
  - Add "Custom/" prefix for NewRelic metrics [#2453](https://github.com/dotnet/orleans/pull/2453/)
  - Ignore named EventWaitHandle when not available in platform [#2462](https://github.com/dotnet/orleans/pull/2462/)
  - Heterogenous silos support  [#2443](https://github.com/dotnet/orleans/pull/2443/)
  - Update to Consul 0.7.0.3 nuget package, because of breaking change in Consul API. [#2498](https://github.com/dotnet/orleans/pull/2498/)
  - Grain Services by @jamescarter-le [#2531](https://github.com/dotnet/orleans/pull/2531/)
  - Expose IMembershipOracle & related interfaces [#2557](https://github.com/dotnet/orleans/pull/2557/)
  - Trigger registration of clients connected to the gateways in the directory when a silo is dead [#2587](https://github.com/dotnet/orleans/pull/2587/)
  - Log Consistency Providers [#1854](https://github.com/dotnet/orleans/pull/1854/)
  - In XML config, if SystemStoreType set to Custom but no ReminderTableAssembly are specified, assume that ReminderServiceProviderType is set to Disabled [#2589](https://github.com/dotnet/orleans/pull/2589/)
  - In config XML, when SystemStoreType is set to MembershipTableGrain, set ReminderServiceType to ReminderTableGrain [#2590](https://github.com/dotnet/orleans/pull/2590/)
  - Service Fabric cluster membership providers [#2542](https://github.com/dotnet/orleans/pull/2542/)
  - Adds optional native JSON support to MySQL [#2288](https://github.com/dotnet/orleans/pull/2288/)
  - Azure table storage throws InconsistentStateException [#2630](https://github.com/dotnet/orleans/pull/2630/)
  - Allow serializers to have multiple [Serializer(...)] attributes [#2611](https://github.com/dotnet/orleans/pull/2611/)
  - Removed GrainStateStorageBridge from GrainCreator to allow better control of the IStorage used when using non-silo unit tests. [#2243](https://github.com/dotnet/orleans/pull/2243/)
  - Failsafe Exception serialization [#2633](https://github.com/dotnet/orleans/pull/2633/)
  - Added a data adapter to azure queue stream provider [#2658](https://github.com/dotnet/orleans/pull/2658/)
  - Client cluster disconnection [#2628](https://github.com/dotnet/orleans/pull/2628/)
- Performance
  - Several major performance improvements: [#2220](https://github.com/dotnet/orleans/pull/2220/), [#2221](https://github.com/dotnet/orleans/pull/2221/), [#2170](https://github.com/dotnet/orleans/pull/2170/), [#2218](https://github.com/dotnet/orleans/pull/2218/), [#2312](https://github.com/dotnet/orleans/pull/2312/), [#2524](https://github.com/dotnet/orleans/pull/2524/), [#2510](https://github.com/dotnet/orleans/pull/2510/), [#2481](https://github.com/dotnet/orleans/pull/2481/), [#2579](https://github.com/dotnet/orleans/pull/2579/)
  - Replace CallContext.LogicalSetData with AsyncLocal [#2200](https://github.com/dotnet/orleans/pull/2200/)
  - Release BinaryTokenStreamWriter buffers after use in more cases. [#2326](https://github.com/dotnet/orleans/pull/2326/)
- Bug fixes
  - Empty deployment Id in Azure [#2230](https://github.com/dotnet/orleans/pull/2230/)
  - Remove zero length check in Protobuf serializer [#2251](https://github.com/dotnet/orleans/pull/2251/)
  - Make PreferLocalPlacement activate in other silos when shutting down [#2276](https://github.com/dotnet/orleans/pull/2276/)
  - Reset GrainClient.ClientInvokeCallback when uninitializing GrainClient [#2299](https://github.com/dotnet/orleans/pull/2299/)
  - Fix ObjectDisposedException in networking layer [#2302](https://github.com/dotnet/orleans/pull/2302/)
  - Reset client gateway reciever buffer on socket reset. [#2316](https://github.com/dotnet/orleans/pull/2316/)
  - Removed calling Trace.Close() from TelemetryConsumer.Close() [#2396](https://github.com/dotnet/orleans/pull/2396/)
  - Removes deadlocking and corrupted hashing in SQL storage provider [#2395](https://github.com/dotnet/orleans/pull/2395/)
  - Fix #2358: Invoke interceptor broken for generic grains [#2502](https://github.com/dotnet/orleans/pull/2502/)
  - Only a hard coded set of statistics were going to telemetry consumers.  Now all non-string statistics are tracked. [#2513](https://github.com/dotnet/orleans/pull/2513/)
  - Fix invocation interception for grain extensions [#2514](https://github.com/dotnet/orleans/pull/2514/)
  - Fix type assertion in AdaptiveDirectoryCacheMaintainer [#2525](https://github.com/dotnet/orleans/pull/2525/)
  - MembershipTableFactory should call InitializeMembershipTable on membership table. [#2537](https://github.com/dotnet/orleans/pull/2537/)
  - CodeGen: fix check on parameters to generic types with serializers [#2575](https://github.com/dotnet/orleans/pull/2575/)
  - EventHubQueueCache failing to write checkpoints on purge [#2613](https://github.com/dotnet/orleans/pull/2613/)
  - Fix code copy-paste errors discovered by Coverity [#2639](https://github.com/dotnet/orleans/pull/2639/)
  - GrainServices are now Started by the Silo on Startup [#2642](https://github.com/dotnet/orleans/pull/2642/) 

## Community Virtual Meetup #12

[Deploying Orleans](https://youtu.be/JrmHfbZH11M) with [Jakub Konecki](https://github.com/jkonecki)
December 8th 2016
[Presentation](https://github.com/dotnet/orleans/raw/gh-pages/Presentations/VM-12%20Orleans-YAMS.pdf)

## [v1.3.1](https://github.com/dotnet/orleans/releases/tag/v1.3.1) November 15th 2016

### Release notes

Improvements and bug fixes since 1.3.0.

- Improvements
  - Ability to specify interleaving per message type (was needed for Orleankka) [#2246](https://github.com/dotnet/orleans/pull/2246/)
  - Support serialization of enums backed by non-Int32 fields [#2237](https://github.com/dotnet/orleans/pull/2237/) 
  - Add TGrainState constraints to document clearly what is needed by folks implementing stateful grains. [#1923](https://github.com/dotnet/orleans/pull/1923/)
  - Serialization fixes [#2295](https://github.com/dotnet/orleans/pull/2295/)
  - Update OrleansConfiguration.xsd with DI info [#2314](https://github.com/dotnet/orleans/pull/2314/)
  - Reroute client messages via a different gateway upon a gateway disconnection [#2298](https://github.com/dotnet/orleans/pull/2298/)
  - Add helper methods to ease ADO.NET configuration [#2291](https://github.com/dotnet/orleans/pull/2291/)
  - EventHubStreamProvider improvements [#2377](https://github.com/dotnet/orleans/pull/2377/)
  - Add queue flow controller that is triggered by silo load shedding. [#2378](https://github.com/dotnet/orleans/pull/2378/)
  - Modify JenkinsHash to be stateless. [#2403](https://github.com/dotnet/orleans/pull/2403/)
  - EventHub flow control customization knobs [#2408](https://github.com/dotnet/orleans/pull/2408/)
- Performance
  - Invoker codegen: methods returning Task<object> do not need Box() calls [#2221](https://github.com/dotnet/orleans/pull/2221/)
  - CodeGen: Avoid wrapping IGrainMethodInvoker.Invoke body in try/catch [#2220](https://github.com/dotnet/orleans/pull/2220/)
  - Remove contention point in GrainDirectoryPartition [#2170](https://github.com/dotnet/orleans/pull/2170/)
  - Optimize the scheduler, remove redundant semaphore and interlocked exchange. [#2218](https://github.com/dotnet/orleans/pull/2218/)
  - Remove delegate allocation [#2312](https://github.com/dotnet/orleans/pull/2312/)
  - Release BinaryTokenStreamWriter buffers after use in more cases. [#2326](https://github.com/dotnet/orleans/pull/2326/)
  - Provide better handling in Grain when the GrainRuntime or GrainIdentity is null [#2338](https://github.com/dotnet/orleans/pull/2338/)
- Bug fixes
  - Reset client gateway reciever buffer on socket reset. [#2316](https://github.com/dotnet/orleans/pull/2316/)
  - Removes potential deadlocking and corrupted hashing in ADO.NET storage provider [#2395](https://github.com/dotnet/orleans/pull/2395/)
  - LoadShedQueueFlowControl cast fix [#2405](https://github.com/dotnet/orleans/pull/2405/)

## Community Virtual Meetup #11

[A monitoring and visualisation show](https://youtu.be/WiAX_eGEuyo) with [Richard Astbury](https://github.com/richorama), [Dan Vanderboom](https://github.com/danvanderboom) and [Roger Creyke](https://github.com/creyke)
October 13th 2016

## [v1.3.0](https://github.com/dotnet/orleans/releases/tag/v1.3.0) October 11th 2016

### Release notes

**NB: There is a subtle breaking change in this release, which is unfortunately easy to miss.**
*If you are using `AzureSilo.Start(ClusterConfiguration config, string deploymentId)` in your code, that overload is gone now, but the new one that replaced it has the same argument signature with a different second argument: `ClusterConfiguration config, string connectionString)`. Deployment ID now has to be passed as part of the `config` argument: config.Globals.DeploymentId. This removed the ambiguous possibility of passing two different Deployment IDs, but unfortunately at the cost of the breaking API change.*

* Bug fixes
  * Empty deployment Id in Azure [#2230](https://github.com/dotnet/orleans/pull/2230/)
  * Remove zero length check in Protobuf serializer [#2251](https://github.com/dotnet/orleans/pull/2251/)
  * Make PreferLocalPlacement revert to RandomPlacement on non-active silos [#2276](https://github.com/dotnet/orleans/pull/2276/)
* Streaming
  * Updated MemoryStreamProvider to support custom message serialization [#2271](https://github.com/dotnet/orleans/pull/2271/)

## [v1.2.4](https://github.com/dotnet/orleans/releases/tag/v1.2.4) October 5th 2016

### Release notes

Bug fix: Prevent null reference exception after clearing PubSubRendezvousGrain state [#2040](https://github.com/dotnet/orleans/pull/2040/)

## [v1.3.0-beta2](https://github.com/dotnet/orleans/releases/tag/v1.3.0-beta2) September 27th 2016

### Release notes

**NB: There is a subtle breaking change in this release, which is unfortunately easy to miss.**
*If you are using `AzureSilo.Start(ClusterConfiguration config, string deploymentId)` in your code, that overload is gone now, but the new one that replaced it has the same argument signature with a different second argument: `ClusterConfiguration config, string connectionString)`. Deployment ID now has to be passed as part of the `config` argument: config.Globals.DeploymentId. This removed the ambiguous possibility of passing two different Deployment IDs, but unfortunately at the cost of the breaking API change.*

* Notable new features
  * Support for geo-distributed multi-cluster deployments [#1108](https://github.com/dotnet/orleans/pull/1108/) [#1109](https://github.com/dotnet/orleans/pull/1109/) [#1800](https://github.com/dotnet/orleans/pull/1800/)
  * Added new Amazon AWS basic Orleans providers [#2006](https://github.com/dotnet/orleans/issues/2006)
  * Support distributed cancellation tokens in grain methods [#1599](https://github.com/dotnet/orleans/pull/1599/)
* Providers
  * Remove confusing parameter from AzureSilo.Start [#2109](https://github.com/dotnet/orleans/pull/2109/)
  * Minimal Service Fabric integration [#2120](https://github.com/dotnet/orleans/pull/2120/)
  * Update blob storage provider to throw on storage exceptions [#1902](https://github.com/dotnet/orleans/pull/1902/)
  * Decode protobuf using MessageParser, not dynamic [#2136](https://github.com/dotnet/orleans/pull/2136/)
  * Service Provider is no longer required by EventHubAdapter [#2044](https://github.com/dotnet/orleans/pull/2044/)
  * Preliminary relational persistence queries [#1682](https://github.com/dotnet/orleans/pull/1682/)
  * Add a function that checks the connection string for use during initialization [#1987](https://github.com/dotnet/orleans/pull/1987/) 
  * A new ADO.NET storage provider that is significantly easier to setup, which replaces the the previous one. This change is not backwards compatible and does not support sharding
  (likely be replaced later with Orleans sharding provider). The most straightforward migration plan is likely to persist the state classes from Orleans application code.
  More information in [#1682](https://github.com/dotnet/orleans/pull/1682) and in [#1682 (comment)](https://github.com/dotnet/orleans/pull/1682#issuecomment-234371701).
  * Support for PostgreSql [#2113](https://github.com/dotnet/orleans/pull/2113/)
  * Memory Storage eTag enforcement less strict. [#1885](https://github.com/dotnet/orleans/pull/1885/)
  * Added option to perform provider commands quietly [#1762](https://github.com/dotnet/orleans/pull/1762/)
  * CreateOrleansTables_SqlServer.sql: Removed support for SQL Server 2000 and 2005 [#1779](https://github.com/dotnet/orleans/pull/1779/)
* Streaming
  * EventHub stream provider made more extensible [#1861](https://github.com/dotnet/orleans/pull/1861/) [#1714](https://github.com/dotnet/orleans/pull/1714/)
  * EventHub stream provider with improved monitoring logging [#1857](https://github.com/dotnet/orleans/pull/1857/) [#2146](https://github.com/dotnet/orleans/pull/2146/)
  * EventHub stream provider time based message purge [#2093](https://github.com/dotnet/orleans/pull/2093/)
  * Add Memory Stream Provider [#2063](https://github.com/dotnet/orleans/pull/2063/)
  * Persistent stream pulling agent now uses exponential backoff [#2078](https://github.com/dotnet/orleans/pull/2078/)
  * Add dynamic adding / removing stream providers functionality. [#1966](https://github.com/dotnet/orleans/pull/1966/)
  * Consistent implicit subscription Id generation. [#1828](https://github.com/dotnet/orleans/pull/1828/)
  * Event hub stream provider EventData to cached data mapping [#1727](https://github.com/dotnet/orleans/pull/1727/)
* Bug fixes
  * CodeGen: fix generated DeepCopy method to call RecordObject earlier [#2135](https://github.com/dotnet/orleans/pull/2135/)
  * Fix support for serializing SByte[] [#2140](https://github.com/dotnet/orleans/pull/2140/)
  * Fix synchronization bug in Orleans/Async/BatchWorker [#2133](https://github.com/dotnet/orleans/pull/2133/)
  * Fix #2119 by allowing full uninitialization in SiloHost [#2127](https://github.com/dotnet/orleans/pull/2127/)
  * Persistent Stream Provider initialization timeout fix. [#2065](https://github.com/dotnet/orleans/pull/2065/)
  * Fix null reference in StreamPubSub grain. [#2040](https://github.com/dotnet/orleans/pull/2040/)
  * Some EventHub stream provider bug fixes [#1760](https://github.com/dotnet/orleans/pull/1760/) [#1935](https://github.com/dotnet/orleans/pull/1935/) [#1921](https://github.com/dotnet/orleans/pull/1921/) [#1922](https://github.com/dotnet/orleans/pull/1922/)
  * Allow comments in configuration XML [#1994](https://github.com/dotnet/orleans/pull/1994/)
  * Fixed null MethodInfo in Interceptors [#1938](https://github.com/dotnet/orleans/pull/1938/)
  * EventHub stream provider Object Pools not pooling fix. [#1937](https://github.com/dotnet/orleans/pull/1937/) 
  * Harden explicit subscription pubsub system [#1884](https://github.com/dotnet/orleans/pull/1884/)
  * Fix #1869. Grain Extensions + method interception should function correctly [#1874](https://github.com/dotnet/orleans/pull/1874/)
  * Fix bug with generic state parameter caused by inconsistent use of grainClassName / genericArgument / genericInterface [#1897](https://github.com/dotnet/orleans/pull/1897/)
  * Throw meaningful exception if grain timer is created outside grain context [#1858](https://github.com/dotnet/orleans/pull/1858/)
  * Do not deactivate Stateless Workers upon grain directory partition shutdown. [#1838](https://github.com/dotnet/orleans/pull/1838/)
  * Fixed a NullReferenceException bug in ClientObserverRegistrar. [#1823](https://github.com/dotnet/orleans/pull/1823/)
* Test
  * Allow liveness config in TestCluster [#1818](https://github.com/dotnet/orleans/pull/1818/)
  * Fix for strange bug in BondSerializer [#1790](https://github.com/dotnet/orleans/pull/1790/)
  * Some improvements for unit testing [#1792](https://github.com/dotnet/orleans/pull/1792/) [#1802](https://github.com/dotnet/orleans/pull/1802/)
* Other
  * Move JSON serialization methods into OrleansJsonSerializer [#2206](https://github.com/dotnet/orleans/pull/2206/)
  * Updated package dependencies for Azure Storage, ServiceBus, ZooKeeperNetEx, Protobuf and codegen related
  * Remove UseStandardSerializer and UseJsonFallbackSerializer options [#2193](https://github.com/dotnet/orleans/pull/2193/) [#2204](https://github.com/dotnet/orleans/pull/2204/)
  * Make IGrainFactory injectable [#2192](https://github.com/dotnet/orleans/pull/2192/)
  * Recover types from ReflectionTypeLoadException [#2164](https://github.com/dotnet/orleans/pull/2164/)
  * Moved Orleans Performance Counters into its own Telemetry Consumer. Now you need to explicitly register the `OrleansPerfCounterTelemetryConsumer` either by code or XML. More information in [#2122](https://github.com/dotnet/orleans/pull/2122) and docs will come later. `Microsoft.Orleans.CounterControl` can still be used to install the performance counters or you can use `InstallUtil.exe OrleansTelemetryConsumers.Counters.dll` to install it without depending on `OrleansCounterControl.exe`
  * New PowerShell client Module [#1990](https://github.com/dotnet/orleans/pull/1990/)
  * Expose property IsLongKey for IAddressable [#1939](https://github.com/dotnet/orleans/pull/1939/)
  * Removed OrleansDependencyInjection package and instead Orleans references Microsoft.Extensions.DepedencyInjection [#1911](https://github.com/dotnet/orleans/pull/1911/) [#1901](https://github.com/dotnet/orleans/pull/1901/) [#1878](https://github.com/dotnet/orleans/pull/1878/)
  * Now using Microsoft.Extensions.DepedencyInjection.ServiceProvider as the default service provider if the user does not override it. Grains are still not being injected automatically unless the user opts in by specifying his own Startup configuration that returns a service provider.
  * Do not require explicitly registering grains in ServiceCollection [#1901](https://github.com/dotnet/orleans/pull/1901/) 
  * ClusterConfiguration extension for setting Startup class [#1842](https://github.com/dotnet/orleans/pull/1842/)
  * Log more runtime statistics on the client. [#1778](https://github.com/dotnet/orleans/pull/1778/)
  * Added ManagementGrain.GetDetailedHosts() [#1794](https://github.com/dotnet/orleans/pull/1794/)
  * Can get a list of active grains in Orleans for monitoring [#1772](https://github.com/dotnet/orleans/pull/1772/) 
  * Rename InstanceName to SiloName. [#1740](https://github.com/dotnet/orleans/pull/1740/)
  * Reworked documentation to use DocFX [#1970](https://github.com/dotnet/orleans/pull/1970/)

## Community Virtual Meetup #10

[The roadmap to Orleans 2.0 with the core team](https://youtu.be/_SbIbYkY88o)
August 25th 2016

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

If your projects still reference those binaries directly from the SDK folder, you need to switch to using [NuGet packages](Installation/Nugets.md) instead. If you are already consuming Orleans via NuGet, you are good.

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
  See this wiki page for advice on how to [convert legacy Orleans grain interface / class projects over to using NuGet packages](Convert-Orleans-v0.9-csproj-to-Use-v1.0-NuGet.md).


## [Community Virtual Meetup #1](http://www.youtube.com/watch?v=6COQ8XzloPg)
[Jakub Konecki](https://github.com/jkonecki) on Event Sourced Grains
December 18th 2014
