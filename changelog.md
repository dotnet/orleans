# Microsoft Orleans Changelog
All notable end-user facing changes are documented in this file.

### [vNext]
*Here are all the changes in `master` branch, and will be moved to the appropriate release once they are included in a published nuget package.
The idea is to track end-user facing changes as they occur.*

- Support for global multi cluster deployoment #1108 #1109 #1800
- Removed OrleansDependencyInjection package and instead Orleans references Microsoft.Extensions.DepedencyInjection #1911 #1901 #1878
  - Now using Microsoft.Extensions.DepedencyInjection.ServiceProvider as the default service provider if the user does not override it.
  - Grains are still not being injected automatically unless the user opts in by specifying his own Startup configuration that returns a service provider.
- Updated Azure Storage dependency to 7.0.0 #1968
- A new ADO.NET storage provider that is significantly easier to setup, which replaces the the previous one. This change is not backwards compatible and does not support sharding
  (likely be replaced later with Orleans sharding provider). The most straightforward migration plan is likely to persist the state classes from Orleans application code.
  More information in [#1682](https://github.com/dotnet/orleans/pull/1682) and in [#1682 (comment)](https://github.com/dotnet/orleans/pull/1682#issuecomment-234371701). 
- Added new Amazon AWS basic Orleans providers [#2006](https://github.com/dotnet/orleans/issues/2006)
- Moved Orleans Performance Counters into its own Telemetry Consumer. Now you need to explicitly register the `OrleansPerfCounterTelemetryConsumer` either by code or XML. More information in [#2122](https://github.com/dotnet/orleans/pull/2122) and docs will come later
	- `Microsoft.Orleans.CounterControl` can still be used to install the performance counters or you can use `InstallUtil.exe OrleansTelemetryConsumers.Counters.dll` to install it without depending on `OrleansCounterControl.exe`

  
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