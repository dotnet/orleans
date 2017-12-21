# Microsoft Orleans Changelog

All notable end-user facing changes are documented in this file.

### [vNext]

*Here are all the changes in `master` branch, and will be moved to the appropriate release once they are included in a published nuget package.
The idea is to track end-user facing changes as they occur.*

### [2.0.0-beta3]

- Breaking changes
  - Remove legacy initialization from Silo class (#3795) 

- Non-breaking improvements
  - Move ServiceId to SiloOptions (#3779)
  - Do not generate serializers for classes which require the use of serialization hooks. (#3790)
  - CodeGen: reduce aggressiveness of serializer generation (#3789)
  - CodeGen: avoid potential confusion between overridden properties in a type hierarchy (#3791)
  - Fix Exception serialization on .NET Core (#3794)
  - Fix shutdown sequence in linuxcontainer (#3796)
  - Fix potential dependency cycles with user-supplied IGrainCallFilter implementations (#3798)
  - Include required provider name in GetStorageProvider exception message. (#3797)
  - Strongly typed endpoint options (#3799)
  - Fix wrong config usages (#3800)
  - Split AWSUtils into separate packages, keep original as meta package (#3720)
  - Add ClusterClientOptions to configure ClusterId (#3801)
  - Refactor transaction abstractions to enable injection of alternate protocols (#3785)
  - Wrap the connection preamble read check in a task (#3729)
  - Split OrleansSQLUtils into separate packages (#3793)

### [2.0.0-beta2]

- Known issues
  - Code generation is too aggressive in generating serializers for most available types instead of just those that are directly used in grain methods. This causes excessive code being generated and compiled.

- Breaking changes
  - Remove `IGrainInvokeInterceptor` that got replaced with `IGrainCallFilter` (#3647)
  - Migrate more configuration settings to typed options (#3492, #3736)
  - Replace DeploymentId with ClusterId and collapse them where both are defined (#3728)

- Non-breaking improvements
  - Better align silo hosting APIs with the future generic Microsoft.Extensions.Hosting.HostBuilder (#3631, #3634, #3696, #3697)
  - Multiple improvements to code generation (#3639, #3643, #3649, #3645, #3666, #3682, #3717)
  - Throw an exception when trying to build a silo with no application assemblies specified (#3644)
  - Multiple improvements to transactions (#3672, #3677, #3730, #3731)
  - Integrate Service Fabric clustering provider with `SiloHostBuilder` (#3638)
  - Split Service Fabric support assembly and NuGet package into two: for silo hosting and clustering (#3638, #3766)
  - Split `OrleansAzureUtils` assembly and NuGet package into more granular assemblies and packages (#3668, #3719)
  - Support for non-static serializers (#3595)
  - Add a timeout for synchronous socket read operations (#3716)
  - Support for multiple fallback serializers (#3688)
  - Enable TCP FastPath support (#3710)
  - Re-introduce run time code generation that can be enabled at silo host build time (#3669)
  - Support for Oracle in AdoNet (SQL) clustering provider (#3576)
  - Disallow creating an observer reference via `CreateObjectReference` from within a grain (#3757)
  - Expedite gateway retries when gateway list is exhausted (#3758)
  - Support for serialization life cycle methods that re-enables serialization of F# types and other such types (#3749)

### [2.0.0-beta1]

- Breaking changes
  - Most packages are now targetting .NET Standard 2.0 (which mean they can be used from either .NET Framework or .NET Core 2.0).
    - These packages still target .NET Framework 4.6.1: `Microsoft.Orleans.TestingSiloHost`, `Microsoft.Orleans.ServiceFabric`, `Microsoft.Orleans.OrleansTelemetryConsumers.Counters`, `Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic` and the PowerShell module.
  - Deprecated the Orleans Logging infrastructure.
    - Orleans now uses the `Microsoft.Extensions.Logging` abstractions package (MEL for short from now on).
    - The legacy Orleans' `Logger` abstraction is preserved as obsolete for backwards compatibility in a new `Microsoft.Orleans.Logging.Legacy` package, but it's just a wrapper that forwards to `ILogger` from MEL. It is recommended that you migrate to it directly.
    - This package also contains a provider for the new MEL abstraction that allows forwarding to `ILogConsumer`, in case the end-user has a custom implementation of that legacy interface. Similarly, it is recommended to rewrite the custom log consumer or telemetry consumer and implement `ILoggerProvider` from MEL instead.
    - The APM methods (TrackXXX) from `Logger` were separated into a new `ITelemetryProducer` interface, and it's currently only being used by Orleans to publish metrics. #3390
    - Logging configuration is no longer parsed from the XML configuration, as the user would have to configure MEL instead.
  - Created a `Microsoft.Orleans.Core.Abstractions` nuget package and moved/refactored several types into it. We plan to rev and do breaking changes to this package very infrequently.
  - NuGet package names were preserved for beta1, but several DLL filenames were renamed.
  - Runtime code generation was removed (`Microsoft.Orleans.OrleansCodeGenerator` package). You should use build-time codegen by installing the `Microsoft.Orleans.OrleansCodeGenerator.Build` package in the grain implementations' and interfaces' projects.
  - `SiloHostBuilder` and `ClientBuilder` are intended to replace the previous ways of initializing Orleans. They are not at 100% parity with `ClusterConfiguration` and `ClientConfiguration` so these are still required for beta1, but they will be eventually deprecated.
  - Note that when using `SiloHostBuilder` and `ClientBuilder`, Orleans will no longer scan every single assembly loaded in the AppDomain by default, and instead you need to be explicit to which ones you use by calling the `AddApplicationPartXXX` methods from each of the builders.
  - Silo membership (and its counterpart Gateway List Provider on the client) and `MessagingOptions` can be configured using the utilities in the `Microsoft.Extensions.Options` package. Before the final 2.0.0 release, the plan is to have everything moved to that configuration infrastructure.
  - Upgraded several dependencies to external packages that are .NET Standard compatible
  - Add support for Scoped services. This means that each grain activation gets its own scoped service provider, and Orleans registers `IGrainActivationContext` that can be injected into Transient or Scoped service to get access to activation specific information and lifecycle events #2856 #3270 #3385
  - Propagate failures in `Grain.OnActivateAsync` to callers #3315
  - Removed obsolete `GrainState` class #3167

- Non-breaking improvements
  - Build-time codegen and silo startup have been hugely improved so that the expensive type discovery happens during build, but at startup it is very fast. This can remove several seconds to startup time, which can be especially noticeable when using `TestCluster` to spin up in-memory clusters all the time #3518
  - Add SourceLink support for easier debugging of nuget packages. You can now follow the following steps to debug Orleans code within your app: https://www.stevejgordon.co.uk/debugging-asp-net-core-2-source #3564
  - Add commit hash information in published assemblies #3575
  - First version of Transactions support (still experimental, and will change in the future, not necessarily back-compatible when it does). Docs coming soon.
  - Fast path for message addressing #3119
  - Add extension for one-way grain calls #3224
  - Google PubSub Stream provider #3210
  - Lease based queue balancer for streams #3196 #3237 #3333
  - Allow localhost connection in AWS SQS Storage provider #3485

- Non-breaking bug fixes
  - Fix occasional NullReferenceException during silo shutdown #3328
  - Avoid serializing delegates and other non-portable types #3240
  - ServiceFabric membership: ensure all silos reach a terminal state #3568
  - Limit RequestContext to messaging layer. It is technically a change in behavior, but not one that end users could have relied upon, but listing it here in case someone notices side-effects due to this #3546

### [1.5.2]

- Non-breaking bug fixes
  - Fix memory leak when using grain timers #3452
  - Fix infrequent `NullReferenceException` during activation collection #3399
  - Improve resiliency in in client message pump loop #3367
  - Service Fabric: fix leak in registration of partition notifications #3411
  - Fixed duplicate stream message cache monitoring bug #3522
  - Several minor bug fixes and perf improvements #3419 #3420 #3489

- Non-breaking improvements
  - Support for PostgreSql as a Storage provider #3384
  - Make JsonConverters inside OrleansJsonSerializer public #3398
  - Set TypeNameHandling in OrleansJsonSerializer according to configuration #3400

### [1.5.1]

- Non-breaking bug fixes
  - Support implicit authentication to DynamoDB (via IAM Roles) #3229
  - Added missing registration for 7-component tuple and several collection interfaces #3282 #3313
  - Support custom silo names in Service Fabric integration #3241
  - Fix scheduling of notification interfaces (which impacted Service Fabric integration) #3290
  - Dispose `IServiceProvider` during client shutdown #3249
  - `ClusterClient.Dispose()` is now equivalent to `Abort()` #3306
  - Avoid `BadImageFormatException` from being written in the log during Silo or client startup #3216
  - Add build-time code generation to `Microsoft.Orleans.OrleansServiceBus` package #3344
  - Several minor bug fixes and perf improvements, as well as reliability in our test code #3234 #3250 #3258 #3283 #3301 #3309 #3311

### [1.5.0]

- Breaking changes
  - Bug fix: Azure storage providers now throw `InconsistenStateException` instead of `StorageException` when eTags do not match #2971
  - Automatically deactivate a grain if it bubbles up `InconsistentStateException` (thrown when there is an optimistic concurrency conflict when writing to storage) #2959
  - Upgraded minimum framework dependency to .NET 4.6.1 #2945
  - Support for non-static client via ClientBuilder (although static GrainClient still works but will be removed in a future version). You can now start many clients in the same process, even if you are inside a Silo #2822.
    There are a few differences though:
    - Several changes to SerializationManager, mainly to make it non-static #2592
      - When deserializing a GrainReference from storage, you might need to re-bind it to the runtime by calling `grain.BindGrainReference(grainFactory)` on it or you would get `GrainReferenceNotBoundException` when attempting to use it #2738
      - Removed `RegisterSerializerAttribute` (and corresponding static Register method for registering a custom serializer). If you were using that, please read https://dotnet.github.io/orleans/Documentation/Advanced-Concepts/Serialization.html#writing-custom-serializers for alternative ways to register your custom serializer #2941
  - Better serialization of `Type` values (but can cause compatibility issues if these were persisted by using the Serialziation Manager) #2952
  - Providers are now constructed using Dependency Injection. The result is that custom providers must have a single public constructor with either no arguments or arguments which can all be injected, or they need to be explicitly registered in the ServiceCollection. #2721 #2980
  - Replaced `CacheSizeInMb` setting with `DataMaxAgeInCache` and `DataMinTimeInCache` in stream providers #3126
  - Renamed the `Catalog.Activation.DuplicateActivations` counter to `Catalog.Activation.ConcurrentRegistrationAttempts` to more accurately reflect what it tracks and its benign nature #3130
  - Change default stream subscription faulting to false in EventHub and Memory stream providers, as is in other providers #2974
  - Allow `IGrainWithGuidCompoundKey` as implicit subscription grain, and sets the stream namespace as the grain key extension (subtle breaking change: previous to 1.5 `IGrainWithGuidCompoundKey` wasn't technically supported, but if you did use it, the grain key extension would have had a `null` string) #3011

- Non-breaking improvements
  - Support for custom placement strategies and directors #2932
  - Grain interface versioning to enable no-downtime upgrades #2837 2837 #3055
  - Expose available versions information in placement context #3136
  - Add support for hash-based grain placement #2944
  - Support fire and forget one-way grain calls using `[OneWay]` method attribute #2993
  - Replace `CallContext.LogicalSetData` with AsyncLocal #2200 #2961
  - Support multiple silo request interceptors #3083
  - Ability to configure `FabricClient` when deploying to Service Fabric #2954
  - Added extension points to EventHubAdapterFactory #2930
  - Added SlowConsumingPressureMonitor for EventHub streams #2873
  - Dispose all registered services in the container when shutting down #2876
  - Try to prevent port collisions when starting in-memory `TestCluster` #2779
  - Deprecated `TestingSiloHost` in favor of `TestCluster` (although the former is still available for this version) #2919
  - Support exceptions with reference cycles in ILBasedExceptionSerializer #2999
  - Add extensibility point to replace the grain activator and finalizer #3002
  - Add statistics to EventHub stream provider ecosystem
  - Add flag to disable FastKill on CTRL-C #3109
  - Avoid benign `DuplicateActivationException` from showing up in the logs #3130
  - Programmable stream subscribe API #2741 #2796 #2909
  - Allow complex streaming filters in `ImplicitStreamSubscriptionAttribute` #2988
  - Make `StreamQueueBalancer` pluggable #3152
  - ServiceFabric: Register `ISiloStatusOracle` implementation in `ServiceCollection` #3160

- Non-breaking bug fixes
  - Improve resiliency in stream PubSub when facing ungraceful shutdown of producers and silos #3003 #3128
  - Fixes to local IP address resolution #3069
  - Fixed a few issues with the Service Fabric membership provider #3059 #3061 #3128
  - Use PostgreSQL synchronous API to avoid locking in DB thread with newer versions of Npgsql #3164
  - Fix race condition on cancelling of `GrainCancellationTokenSource` #3168
  - Fixes and improvements for the Event Hub stream provider #3014 #3096 #3041 #3052 #2989
  - Fix `NullReferenceException` when no `LogConsistencyProvider` attribute is provided #3158
  - Several minor bug fixes and perf improvements, as well as reliability in our test code
  
### [v1.4.2] (changes since 1.4.1)

- Non-breaking improvements
  - Generate serializers for more types #3035
  - Improvements to GrainServices API #2839
  - Add extensibility point to replace the grain activator #3002
  - Expose IsOrleansShallowCopyable for external custom serializers #3077
  - Detect if activation is in `Deactivating` state for too long and remove it from the directory if needed #3082
  - Support grains with key extensions containing `+` symbols #2956
  - Allow `TimeSpan.MaxValue` in configuration #2985
  - Support for us-gov-west-1 as a possible AWS region endpoint #3017
  - Support `CultureInfo` via built-in serializer #3036
- Non-breaking bug fixes
  - Change how message expiration is handled to account for server clock skew #2922
  - Fix various unhandled exceptions happening during client closing #2962
  - SMS: Ensure items are copied before yielding the thread in OnNextAsync #3048 #3058
  - Remove unneeded extra constructors to play nicer with some non fully-conforming 3rd party containers #2996 #3074

### [v1.4.1]

- Improvements
  - Fix a cleanup issue in TestCluster after a failure #2734
  - Remove unnecessary service registration of IServiceProvider to itself, which improves support for 3rd party containers #2749
  - Add a timeout for socket connection #2791
  - Support for string grain id in OrleansManager.exe #2815
  - Avoid reconnection to gateway no longer in the list returned by IGatewayListProvider #2824
  - Handle absolute path in IntermediateOutputPath to address issue 2864 #2865, #2871
  - Rename codegen file to be excluded from code analyzers #2872
  - ProviderTypeLoader: do not enumerate types in ReflectionOnly assembly. #2869
  - Do not throw when calling Stop on AsynchQueueAgent after it was disposed.
- Bug fixes
  - NodeConfiguration.AdditionalAssemblyDirectories was not 'deeply' copied in the copy constructor #2758
  - Fix AsReference() in generated code for null values #2756
  - Avoid a NullReferenceException in SerializationManager.Register(...) #2768
  - Fix missing check for empty deployment id #2786
  - Fix to make OrleansPerfCounterTelemetryConsumer still work for grain-specific counters. (part of #2807)
  - Fix typos in format strings #2853
  - Fix null reference exception in simple queue cache. #2829 

### [v1.4.0]

- Breaking changes
  - All grain instances and providers are constructed using the configured Dependency Injection container. The result is that all grains must have a single parameterless public constructor or single constructor with arguments which can all be injected. If no container is configured, the default container will be used. [#2485](https://github.com/dotnet/orleans/pull/2485)
- Known issues
  - When the silo starts up, it will register IServiceProvider in the container, which can be a circular reference registration when using 3rd party containers such as AutoFac. This is being addressed for 1.4.1, but there is a simple workaround for it at [#2747](https://github.com/dotnet/orleans/issues/2747)
  - The build-time code generator required (and automatically added) a file named `Properties\orleans.codegen.cs` to the project where codegen was being ran. The new MSBuild targets no longer do that, so when upgrading a solution with a previous version of Orleans, you can safely delete this orleans.codegen.cs file from your grain projects.

- Improvements
  - Support for grains with generic methods #2670
  - Do native PE files check before assembly loading to avoid misleading warnings on startup #2714
  - Throw explicit exception when using streams not in Orleans Context #2683
  - Several improvements to `JournaledGrain` API #2651 #2720
  - Allow overriding MethodId using [MethodId(id)] on interface methods #2660
- Bug fixes
  - EventHubSequenceToken not used consistently #2724
  - Support grains with generic state where the state param do not match the grain type params #2715
  - Fix ServiceFabric IFabricServiceSiloResolver registration in DI container #2712
  - Fix ConditionalCheckFailedException when updating silo 'IAmAlive' field in DynamoDB #2678
  - Ensure DynamoDB Gateway Provider only returns silos with a proxy port configured #2679
  - Fix e-Tag issue in AzureBlobStorage when calling ClearStateAsync (#2669)
  - Other minor fixes: #2729 #2691

### [v1.4.0-beta]

- Noteworthy breaking changes:
  - Azure table storage throws InconsistentStateException #2630
- Improvements
  - Optional IL-based fallback serializer #2162
  - IncomingMessageAcceptor sockets change from APM to EAP #2275
  - Show clearer error when ADO.NET provider fails to init #2303, #2306
  - In client, when a gateway connection close reroute not yet sent message to another gateway #2298
  - MySQL Script: Minor syntax tweak to support previous server versions #2342
  - Azure Queue provider message visibility config #2401
  - Propagate exceptions during message body deserialization #2364
  - Check IAddressable before DeepCopy #2383
  - Modified stream types to not use fallback serializer and allow external #2330
  - Add "Custom/" prefix for NewRelic metrics #2453
  - Ignore named EventWaitHandle when not available in platform #2462
  - Heterogenous silos support  #2443
  - Update to Consul 0.7.0.3 nuget package, because of breaking change in Consul API. #2498
  - Grain Services by @jamescarter-le #2531
  - Expose IMembershipOracle & related interfaces #2557
  - Trigger registration of clients connected to the gateways in the directory when a silo is dead #2587
  - Log Consistency Providers #1854
  - In XML config, if SystemStoreType set to Custom but no ReminderTableAssembly are specified, assume that ReminderServiceProviderType is set to Disabled #2589
  - In config XML, when SystemStoreType is set to MembershipTableGrain, set ReminderServiceType to ReminderTableGrain #2590
  - Service Fabric cluster membership providers #2542
  - Adds optional native JSON support to MySQL #2288
  - Allow serializers to have multiple [Serializer(...)] attributes #2611
  - Removed GrainStateStorageBridge from GrainCreator to allow better control of the IStorage used when using non-silo unit tests. #2243
  - Failsafe Exception serialization #2633
  - Added a data adapter to azure queue stream provider #2658
  - Client cluster disconnection #2628
  - Tooling improvements in build-time codegen #2523
- Performance
  - Several major performance improvements: #2220, #2221, #2170, #2218, #2312, #2524, #2510, #2481, #2579
  - Release BinaryTokenStreamWriter buffers after use in more cases. #2326
- Bug fixes
  - Empty deployment Id in Azure #2230
  - Remove zero length check in Protobuf serializer #2251
  - Make PreferLocalPlacement activate in other silos when shutting down #2276
  - Reset GrainClient.ClientInvokeCallback when uninitializing GrainClient #2299
  - Fix ObjectDisposedException in networking layer #2302
  - Reset client gateway reciever buffer on socket reset. #2316
  - Removed calling Trace.Close() from TelemetryConsumer.Close() #2396
  - Removes deadlocking and corrupted hashing in SQL storage provider #2395
  - Fix #2358: Invoke interceptor broken for generic grains #2502
  - Only a hard coded set of statistics were going to telemetry consumers.  Now all non-string statistics are tracked. #2513
  - Fix invocation interception for grain extensions #2514
  - Fix type assertion in AdaptiveDirectoryCacheMaintainer #2525
  - MembershipTableFactory should call InitializeMembershipTable on membership table. #2537
  - CodeGen: fix check on parameters to generic types with serializers #2575
  - EventHubQueueCache failing to write checkpoints on purge #2613
  - Fix code copy-paste errors discovered by Coverity #2639
  - GrainServices are now Started by the Silo on Startup #2642

### [v1.3.1]

- Improvements
  - Ability to specify interleaving per message type (was needed for Orleankka) #2246
  - Support serialization of enums backed by non-Int32 fields #2237 
  - Add TGrainState constraints to document clearly what is needed by folks implementing stateful grains. #1923
  - Serialization fixes #2295
  - Update OrleansConfiguration.xsd with DI info #2314
  - Reroute client messages via a different gateway upon a gateway disconnection #2298
  - Add helper methods to ease ADO.NET configuration #2291
  - EventHubStreamProvider improvements #2377
  - Add queue flow controller that is triggered by silo load shedding. #2378
  - Modify JenkinsHash to be stateless. #2403
  - EventHub flow control customization knobs #2408
- Performance
  - Invoker codegen: methods returning Task<object> do not need Box() calls #2221
  - CodeGen: Avoid wrapping IGrainMethodInvoker.Invoke body in try/catch #2220
  - Remove contention point in GrainDirectoryPartition #2170
  - Optimize the scheduler, remove redundant semaphore and interlocked exchange. #2218
  - Remove delegate allocation #2312
  - Release BinaryTokenStreamWriter buffers after use in more cases. #2326
  - Provide better handling in Grain when the GrainRuntime or GrainIdentity is null #2338
- Bug fixes
  - Reset client gateway reciever buffer on socket reset. #2316
  - Removes potential deadlocking and corrupted hashing in ADO.NET storage provider #2395
  - LoadShedQueueFlowControl cast fix #2405

### [v1.3.0]

- Bug fixes:
  - Ignore empty deployment Id in Azure #2230 
  - Remove zero length check in Protobuf serializer #2251 
  - Make PreferLocalPlacement revert to RandomPlacement on non-active silos #2276 
- Updated MemoryStreamProvider to support custom message serialization #2271 

### [v1.3.0-beta2]

- Support for geo-distributed multi-cluster deployments #1108 #1109 #1800
- Providers
  - Remove confusing parameter from AzureSilo.Start #2109
  - Minimal Service Fabric integration #2120
  - Update blob storage provider to throw on storage exceptions #1902
  - Decode protobuf using MessageParser, not dynamic #2136
  - Service Provider is no longer required by EventHubAdapter #2044
  - Preliminary relational persistence queries #1682
  - Add a function that checks the connection string for use during initialization #1987
  - Added new Amazon AWS basic Orleans providers [#2006](https://github.com/dotnet/orleans/issues/2006)
  - A new ADO.NET storage provider that is significantly easier to setup, which replaces the the previous one. This change is not backwards compatible and does not support sharding
  (likely be replaced later with Orleans sharding provider). The most straightforward migration plan is likely to persist the state classes from Orleans application code.
  More information in [#1682](https://github.com/dotnet/orleans/pull/1682) and in [#1682 (comment)](https://github.com/dotnet/orleans/pull/1682#issuecomment-234371701).
  - Support for PostgreSql #2113
  - Memory Storage eTag enforcement less strict. #1885
  - Added option to perform provider commands quietly #1762
  - CreateOrleansTables_SqlServer.sql: Removed support for SQL Server 2000 and 2005 #1779
- Streaming
  - EventHub stream provider made more extensible #1861 1714
  - EventHub stream provider with improved monitoring logging #1857 #2146
  - EventHub stream provider time based message purge #2093
  - Add Memory Stream Provider #2063
  - Persistent stream pulling agent now uses exponential backoff #2078
  - Add dynamic adding / removing stream providers functionality. #1966
  - Consistent implicit subscription Id generation. #1828
  - Event hub stream provider EventData to cached data mapping #1727
- Bug fixes
  - CodeGen: fix generated DeepCopy method to call RecordObject earlier #2135
  - Fix support for serializing SByte[] #2140
  - Fix synchronization bug in Orleans/Async/BatchWorker #2133
  - Fix #2119 by allowing full uninitialization in SiloHost #2127
  - Persistent Stream Provider initialization timeout fix. #2065
  - Some EventHub stream provider bug fixes #1760 #1935 #1921 #1922
  - Allow comments in configuration XML #1994
  - Fixed null MethodInfo in Interceptors #1938
  - Object Pools not pooling fix. #1937 
  - Harden explicit subscription pubsub system #1884
  - Fix #1869. Grain Extensions + method interception should function correctly #1874
  - Fix bug with generic state parameter caused by inconsistent use of grainClassName / genericArgument / genericInterface #1897
  - Throw meaningful exception if grain timer is created outside grain context #1858
  - Do not deactivate Stateless Workers upon grain directory partition shutdown. #1838
  - Fixed a NullReferenceException bug in ClientObserverRegistrar. #1823
- Test
  - Allow liveness config in TestCluster #1818
  - Fix for strange bug in BondSerializer #1790
  - Some improvements for unit testing #1792 #1802
- Other
  - Move JSON serialization methods into OrleansJsonSerializer #2206
  - Updated package dependencies for Azure Storage, ServiceBus, ZooKeeperNetEx, Protobuf and codegen related
  - Remove UseStandardSerializer and UseJsonFallbackSerializer options #2193 #2204
  - Make IGrainFactory injectable #2192
  - Recover types from ReflectionTypeLoadException #2164
  - Moved Orleans Performance Counters into its own Telemetry Consumer. Now you need to explicitly register the `OrleansPerfCounterTelemetryConsumer` either by code or XML. More information in [#2122](https://github.com/dotnet/orleans/pull/2122) and docs will come later. `Microsoft.Orleans.CounterControl` can still be used to install the performance counters or you can use `InstallUtil.exe OrleansTelemetryConsumers.Counters.dll` to install it without depending on `OrleansCounterControl.exe`
  - New PowerShell client Module #1990
  - Expose property IsLongKey for IAddressable #1939
  - Removed OrleansDependencyInjection package and instead Orleans references Microsoft.Extensions.DepedencyInjection #1911 #1901 #1878
  - Now using Microsoft.Extensions.DepedencyInjection.ServiceProvider as the default service provider if the user does not override it. Grains are still not being injected automatically unless the user opts in by specifying his own Startup configuration that returns a service provider.
  - Do not require explicitly registering grains in ServiceCollection #1901
  - Support cancellation tokens in grain method signatures #1599
  - ClusterConfiguration extension for setting Startup class #1842
  - Log more runtime statistics on the client. #1778
  - Added ManagementGrain.GetDetailedHosts() #1794
  - Can get a list of active grains in Orleans for monitoring #1772 
  - Rename InstanceName to SiloName. #1740
  - Reworked documentation to use DocFX #1970

### [v1.2.4]

- Bug fix: Prevent null reference exception after clearing PubSubRendezvousGrain state #2040
  
### [v1.2.3]

- Ability to force creation of Orleans serializers for types not marked with [Serializable] by using GenerateSerializer, KnownType or KnownAssembly.TreatTypesAsSerializable #1888 #1864 #1855
- Troubleshooting improvements:
  - Fixed stacktrace preservation in exceptions from grain calls (bug introduced in 1.2.0) #1879 #1808
  - Better messaging when silo fails to join due to initial connectivity problems #1866
  - Throw meaningful exception if grain timer is created outside grain context #1858
- Bug fixes:
  - Do not deactivate Stateless Workers upon grain directory partition shutdown #1838
  - interception works with Streams and grain extensions #1874
  - Memory Storage provider properly enforces etags for any state that has been added or removed, but does not enforce etags for newly added state. #1885
  - Other minor bug fixes #1823
- Known issues:
  - It is not advisable for your Orleans application to depend on WindowsAzure.Storage >= 7.0 due to #1912. This new constraint applies to previously released Orleans versions too. Will be fixed in 1.3.0.
  
### [v1.2.2]

- Bugfix: Memory Storage provider no longer throws NullReferenceException after the grain state is cleared. #1804
- Microsoft.Orleans.OrleansCodeGenerator.Build package updated to not add the empty orleans.codegen.cs content file at install time, and instead create it at build time (should be more compatible with NuGet Transitive Restore). #1720
- Added GrainCreator abstraction to enable some unit testing scenarios. #1802 #1792
- ServiceBus package dependency upgraded to 3.2.2 #1758

### [v1.2.1]

- Bug fixes:
  - SupressDuplicateDeads: Use SiloAddress.Endpoint instead of InstanceName. #1728
  - Added support for complex generic grain parameters #1732
  - Fix race condition bugs in LocalReminderService #1757

### [v1.2.0]

- Major improvements
  - Added an EventHub stream provider based on the same code that is used in Halo 5.
  - Increased throughput by between 5% and 26% depending on the scenario. #1586
  - Improved propagation of exception, so that the caller gets the originally thrown exception instead of an AggregateException wrapping it. #1356
  - Grain state doesn't have to extend GrainState anymore (marked as [Obsolete]) and can be a simple POCO class.
  - Added support for per-grain-class and global server-side interceptors. #965 #963
  - Added support for using Consul as a Membership Provider. #1267
  - Azure storage 7.0 compatibility #1704.
- Codegen & serialization
  - Added support for generic type constraints in codegen. #1137
  - Added support for Newtonsoft.Json as a fallback serializer. #1047
  - Added generation of serializers for type arguments of IAsyncObserver<T>. #1319
  - Improved support for F# interfaces. #1369
  - Consolidated two compile time codegen NuGet packages into one Microsoft.Orleans.OrleansCodeGenerator.Build. Microsoft.Orleans.Templates.Interfaces and Microsoft.Orleans.Templates.Grains are now meta-packages for backward compatibility only. #1501
  - Moved to Newtonsoft.Json 7.0.1. #1302
- Programmatic config
  - Added helper methods for programmatic test configuration. #1411
  - Added helper methods to AzureClient and AzureSilo for easier programmatic config. #1622
  - Added extension methods for using programmatic config. #1623
  - Remove config filed from Server and Client NuGet packages. #1629
- Other
  - Improved support for SQL membership, reminders, and grain storage. #1060
  - Added a storage provider for Azure Blob (graduated from OrleansContrib). #1376
  - Start Reminder Service initial load in the background. #1520
  - Added automatic cleanup of dead client stream producers and consumers. #1429 #1669
  - Added GetPrimaryKeyString extension method for IAddressable. #1675
  - Added support for additional application directories. #1674
  - Migrated all but 30 functional tests to GitHub.
  - Support C# 6. #1479
  - Switched to xUnit for testing as a step towards CoreCLR compatibility. #1455
  - Added ability to throw exceptions that occur when starting silo #1711.
- Many other fixes and improvements.

### [v1.1.3]

- Bug fixes:
  - #1345 Initialize SerializationManager before CodeGeneratorManager
  - #1348 Avoid unnecessary table scan when finding reminder entries to delete
  - #1351 Stop a stuck BlockingCollection.Take operation that caused thread leak on the client.
  - #1381 Fixed Azure table property being not sanitized.
  - #1384 Fixed String.Format arguments in DetailedGrainReport.ToString()
  - #1405 Increment and DecrementMetric methods in Orleans.TraceLogger had same body
  - #1414 Update the custom serializer warning message to adequately reflect the OSS status of Orleans
  - #1503 Fix retry timeout when running under debugger
  - #1478 Networking bug fix: Reset receive buffer on error.
  - #1518 Fixed performance regression in networking
  - #1520 Start ReminderService initial load in the background
  - #1534 Safe load of types from failing assemblies in TypeUtils.GetTypes

### [v1.1.2]

- Bug fixes (primarily for codegen and serializer corner cases):
  - #1137 Add support for generic type constraints in codegen
  - #1178 Correctly specify struct type constraint in generated code
  - #1182 fix issue:GetReminder throws exception when reminder don't exists #1167
  - #1240 Cleanup/fix usage of IsNested vs. IsNestedXXX & serialize nested types.
  - #1241 Correctly serialize [Obsolete] fields and properties.
  - #1249 Nested serialization of Guid with Json serializer.
  - #1261 Fix a race in StreamConsumer.SubscribeAsync.
  - #1280 fix deepcopy issue #1278
  - #1284 Check declaring types when performing accessibility checks for code gen.
  - #1285 Allow to configure PubSub for SMS.
  - #1270 Make Namespace access modifier public in ImplicitStreamSubscriptionAttribute. Add Provider property.

### [v1.1.1]

- Bug fixes:
  - #1134 Missing argument to trace format in TraceLogger.Initialize
  - #1195 Make ConsoleText resilient to ObjectDisposedExceptions

### [v1.1.0]

- New Roslyn-based codegen, compile time and run time
- Public APIs:
  - Core API for Event Sourcing
  - Most methods of Grain class are now virtual
  - ASP.NET vNext style Dependency Injection for grains
  - New telemetry API
- Portability:
  - Support for C# 6.0
  - Improved support for F# and VB
  - Code adjustments towards CoreCLR compliance
  - Orleans assemblies are not strong-named anymore
- SQL:
  - OrleansSQLUtils.dll for SQL-related functionality
  - MySQL is now supported as a cluster membership store
  - Storage provider for SQL Server
- Serialization:
  - Support for pluggable external serializers
  - Bond serializer plugin
  - Support for Json.Net as a fallback serializer
  - Added [KnownType] attribute for generating serializers for arbitrary types
- Upgraded to Azure Storage 5.0
- Upgraded to .NET 4.5.1
- Other fixes and improvements
