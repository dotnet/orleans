## Microsoft Orleans Changelog

All notable end-user facing changes are documented in this file.

### [vNext]

*Here are all the changes in `master` branch, and will be moved to the appropriate release once they are included in a published nuget package.
The idea is to track end-user facing changes as they occur.*

### [3.1.6] (changes since 3.1.5)

- Non-breaking improvements
  - Added eventIndex (#6467)
  - Send rejections for messages enqueued on stopped outbound queue (#6474)
  - Stopped WorkItemGroup logging enhancement (#6483)
  - Streamline LINQ/Enumerable use (#6482)

- Non-breaking bug fixes
  - Gossip that the silo is dead before the outbound queue gets closed (#6480)
  - Fix a race condition in LifecycleSubject (#6481)

### [3.1.5] (changes since 3.1.4)

- Non-breaking improvements
  - Don't use iowait in cpu calcs on linux (#6444)
  - TLS: specify an application protocol to satisfy ALPN (#6455)
  - Change the error about not supported membership table cleanup functionality into a warning. (#6447)
  - Update obsoletion warning for ISiloBuilderConfigurator (#6461)
  - Allow GatewayManager initialization to be retried (#6459)

- Non-breaking bug fixes
  - Consistently sanitize RowKey & PartitionKey properties for Azure Table Storage reminders implementation (#6460)

### [3.1.4] (changes since 3.1.3)

- Non-breaking improvements
  - Reduce port clashes in TestCluster (#6399, #6413)
  - Use the overload of ConcurrentDictionary.GetOrAdd that takes a method (#6409)
  - Ignore not found exception when clearing azure queues (#6419)
  - MembershipTableCleanupAgent: dispose timer if cleanup is unsupported (#6415)
  - Allow grain call filters to retry calls (#6414)
  - Avoid most cases of loggers with non-static category names (#6430)
  - Free SerializationContext and DeserializationContext between calls (#6433)

- Non-breaking bug fixes
  - Reminders period overflow issue in ADO.NET Reminders Table (#6390)
  - Read only the body segment from EventData (#6412)

### [3.1.3] (changes since 3.1.2)

- Breaking changes (for rolling upgrades from 3.1.0 and 3.1.2 running on .NET Core 3.1)
  - Omit assembly name for all types from System namespace during codegen (#6394)
  - Fix System namespace classification in Orleans.CodeGenerator (#6396)

- Non-breaking improvements
  - Amended Linux stats registration to add services on Linux only (#6375)
  - Update performance counter dependencies (#6397)

### [3.1.2] (changes since 3.1.0)

- Non-breaking improvements
  - Remove new() constraint for grain persistence (#6351)
  - Improve TLS troubleshooting experience (#6352)
  - Remove unnecessary RequestContext.Clear in networking (#6357)
  - Cleanup GrainBasedReminderTable (#6355)
  - Avoid using GrainTimer in non-grain contexts (#6342)

- Non-breaking bug fixes
  - Fix CleanupDefunctSiloMembership & MembershipTableTests (#6344)
  - Schedule IMembershipTable.CleanupDefunctSiloEntries more frequently (#6346)
  - CodeGenerator fixes (#6347)
  - Fix handling of gateways in Orleans.TestingHost (#6348)
  - Avoid destructuring in log templates (#6356)
  - Clear RequestContext after use (#6358)

### [3.1.0] (changes since 3.0.0)

- Non-breaking improvements
  - Azure table grain storage inconsistent state on not found (#6071)
  - Removed silo status check before cleaing up system targets from… (#6072)
  - Do not include grain identifier in the ILogger category name (#6122)
  - Specify endpoint AddressFamily in Socket constructor (#6168)
  - Make IFatalErrorHandler public so that it can be replaced by users (#6170)
  - Initial cross-platform build unification (#6183)
  - Fix 'dotnet pack --no-build' (#6184)
  - Migrate 'src' subdirectory to new code generator (#6188)
  - Allow MayInterleaveAttribute on base grains. Fix for issue #6189 (#6192)
  - Multi-target Orleans sln and tests (#6190)
  - Serialization optimizations for .NET Core 3.1 (#6207)
  - Shorten ConcurrentPing_SiloToSilo (#6211)
  - Add OrleansDebuggerHelper.GetGrainInstance to aid in local debugging (#6221)
  - Improve logging and tracing, part 1 (#6226)
  - Mark IGatewayListProvider.IsUpdatable obsolete and avoid blocking refresh calls when possible (#6236)
  - Expose IClusterMembershipService publicly (#6243)
  - Minor perf tweak for RequestContext when removing last item (#6216)
  - Change duplicate activation to a debug-level message (#6246)
  - Add support Microsoft.Data.SqlClient provider, fix #6229 (#6238)
  - TestCluster: support configurators for IHostBuilder & ISiloBuilder (#6250)
  - Adds MySqlConnector library using invariant MySql.Data.MySqlConnector (#6251)
  - Expose exception when initializing PerfCounterEnvironmentStatistics (#6260)
  - Minor serialization perf improvements for .NET Core (#6212)
  - Multi-target TLS connection middleware to netcoreapp3.1 and netstandard2.0 (#6154)
  - Fix codegen incremental rebuild (#6258)
  - CodeGen: combine cache file with args file and fix incremental rebuild (#6266)
  - Avoid performing a lookup when getting WorkItemGroup for SchedulingContext (#6265)
  - Membership: require a minimum grace period during ungraceful shutdown (#6267)
  - Provide exception to FailFast in FatalErrorHandler (#6272)
  - Added support for PAY_PER_REQUEST BillingMode (#6268)
  - Use RegionEndpoint.GetBySystemName() to resolve AWS region (#6269)
  - Support Grain Persistency TTL On dynamo DB (#6275, #6287)
  - Replaced throwing Exception to Logger.LogWarning (#6286)
  - Added ability to skip client TLS authentication. (#6302)
  - Use current element for SimpleQueueCacheCursor.Element (#6299)
  - Manual stats dump #6310 (#6311)
  - Fix SQL Server connection string (#6320)
  - Don't set ServiceUrl when region is provided. (#6327)
  - Explicit setting for UseProvisionedThroughput (#6328)
  - Add explicit references to System.Diagnostics.EventLog and System.Security.Cryptography.Cng to fix build warnings. (#6329)
  - Change NETSTANDARD2_1 preprocessor directives to NETCOREAPP (#6332)
  - Implement CleanupDefunctSiloEntries for DynamoDB membership provider (#6333)

- Non-breaking bug fixes
  - Consul: support extended membership protocol (#6095)
  - Fix routing of gateway count changed events to registered servi… (#6102)
  - Allow negative values in TypeCodeAttribute. Fixes #6114 (#6127)
  - DynamoDB: support extended membership protocol (#6126)
  - Redact logged connection string in ADO storage provider during init (#6139)
  - Fixed CodeGenerator.MSBuild cannot ResolveAssembly in .NetCore 3.0 (#6143)
  - CodeGen: fix ambiguous reference to Orleans namespace (#6171)
  - Avoid potential NullReferenceException when re-initializing statistics (#6179)
  - Close ConnectionManager later in shutdown stage (#6217)
  - Avoid capturing ExecutionContext in GrainTimer and other timers (#6234)
  - Fix code gen for ValueTask (#6285)
  - Add missing dependency to Orleans.CodeGenerator (#6297)
  - Add System.Threading.Tasks.Extensions dependency to Abstractions (#6301)
  - Propagate TestClusterOptions.GatewayPerSilo value in TestClusterOptions.ToDictionary() (#6326)
  - Avoid registering Gateway in DI since it can be null (#6312)

### [3.1.0-rc3] (changes since 3.1.0-rc2)

- Non-breaking bug fixes
  - Add System.Threading.Tasks.Extensions dependency to Abstractions (#6301)

### [3.1.0-rc2] (changes since 3.1.0-rc1)

- Non-breaking bug fixes
  - Add missing dependency to Orleans.CodeGenerator (#6297)

### [3.1.0-rc1] (changes since 3.0.2)

- Non-breaking improvements
  - Initial cross-platform build unification (#6183)
  - Fix 'dotnet pack --no-build' (#6184)
  - Migrate 'src' subdirectory to new code generator (#6188)
  - Allow MayInterleaveAttribute on base grains. Fix for issue #6189 (#6192)
  - Multi-target Orleans sln and tests (#6190)
  - Serialization optimizations for .NET Core 3.1 (#6207)
  - Shorten ConcurrentPing_SiloToSilo (#6211)
  - Add OrleansDebuggerHelper.GetGrainInstance to aid in local debugging (#6221)
  - Improve logging and tracing, part 1 (#6226)
  - Mark IGatewayListProvider.IsUpdatable obsolete and avoid blocking refresh calls when possible (#6236)
  - Expose IClusterMembershipService publicly (#6243)
  - Minor perf tweak for RequestContext when removing last item (#6216)
  - Change duplicate activation to a debug-level message (#6246)
  - Add support Microsoft.Data.SqlClient provider, fix #6229 (#6238)
  - TestCluster: support configurators for IHostBuilder & ISiloBuilder (#6250)
  - Adds MySqlConnector library using invariant MySql.Data.MySqlConnector (#6251)
  - Expose exception when initializing PerfCounterEnvironmentStatistics (#6260)
  - Minor serialization perf improvements for .NET Core (#6212)
  - Multi-target TLS connection middleware to netcoreapp3.1 and netstandard2.0 (#6154)
  - Fix codegen incremental rebuild (#6258)
  - CodeGen: combine cache file with args file and fix incremental rebuild (#6266)
  - Avoid performing a lookup when getting WorkItemGroup for SchedulingContext (#6265)
  - Membership: require a minimum grace period during ungraceful shutdown (#6267)
  - Provide exception to FailFast in FatalErrorHandler (#6272)
  - Added support for PAY_PER_REQUEST BillingMode (#6268)
  - Use RegionEndpoint.GetBySystemName() to resolve AWS region (#6269)
  - Support Grain Persistency TTL On dynamo DB (#6275, #6287)
  - Replaced throwing Exception to Logger.LogWarning (#6286)

- Non-breaking bug fixes
  - Avoid potential NullReferenceException when re-initializing statistics (#6179)
  - Close ConnectionManager later in shutdown stage (#6217)
  - Avoid capturing ExecutionContext in GrainTimer and other timers (#6234)
  - Fix code gen for ValueTask (#6285)

### [2.4.5] (changes since 2.4.4)

- Non-breaking improvements
  - Make IFatalErrorHandler public so that it can be replaced by users (#6170)
  - Allow MayInterleaveAttribute on base grains. Fix for issue #6189 (#6192)

- Non-breaking bug fixes
  - Azure table grain storage inconsistent state on not found (#6071)
  - Removed silo status check before cleaning up system targets from… (#6072)
  - CodeGen: fix ambiguous reference to Orleans namespace (#6171)

### [3.0.2] (changes since 3.0.1)

- Non-breaking improvements
  - Specify endpoint AddressFamily in Socket constructor (#6168)
  - Make IFatalErrorHandler public so that it can be replaced by users (#6170)
 
- Non-breaking bug fixes
  - CodeGen: fix ambiguous reference to Orleans namespace (#6171)

### [3.0.1] (changes since 3.0.0)

- Non-breaking improvements
  - Azure table grain storage inconsistent state on not found (#6071)
  - Removed silo status check before cleaing up system targets from… (#6072)
  - Do not include grain identifier in the ILogger category name (#6122)
 
- Non-breaking bug fixes
  - Consul: support extended membership protocol (#6095)
  - Fix routing of gateway count changed events to registered servi… (#6102)
  - Allow negative values in TypeCodeAttribute. Fixes #6114 (#6127)
  - DynamoDB: support extended membership protocol (#6126)
  - Redact logged connection string in ADO storage provider during init (#6139)
  - Fixed CodeGenerator.MSBuild cannot ResolveAssembly in .NetCore 3.0 (#6143)

### [2.4.4] (changes since 2.4.3)

- Non-breaking improvements
  - Add warning message at startup (#6041)
  - Implement CleanupDefunctSiloEntries for Consul membership provider (#6056)
  - Fixed typo in exception (#6091)

- Non-breaking bug fixes
  - Fix potential rare NullReferenceException in GrainTimer (#6043)
  - Consul: support extended membership protocol (#6095)
  - Fix routing of gateway count changed events to registered servi… (#6102)
  - Allow negative values in TypeCodeAttribute. Fixes #6114 (#6127)
  - DynamoDB: support extended membership protocol (#6126)
  - Redact logged connection string in ADO storage provider during init (#6139)

### [3.0.0] (changes since 3.0.0-rc2)

- Non-breaking improvements
  - Added consistent logging for all messages dropped due to expiry (#6053)
  - Implement CleanupDefunctSiloEntries for Consul membership provider (#6056)
  - Add remark about SQL scripts to client/silo builder documentation (#6062)

### [3.0.0-rc2] (changes since 3.0.0-rc1)

- Non-breaking improvements
  - Default to cleaning up dead silo entries in the cluster membership table after 7 days. (#6032)
  - Reduce log noise in SiloConnection (#6037)
  - Add separate SiloMessagingOptions.SystemResponseTimeout option for SystemTarget calls (#6046)
  - Added structured logging (#6045)
  - Transactions: support larger state sizes in Azure Table Storage (#6047)
  - Add warning message at startup (#6041)
  - Add TLS middleware with sample (#6035)
  - Prevent Orleans + Kestrel from interfering with each other's networking services (#6042)
  - Remove SQL scripts from AdoNet NuGet packages. (#6049)

- Non-breaking bug fixes
  - Add an explicit reference to Microsoft.Bcl.AsyncInterfaces pack… (#6031)
  - Fix potential rare NullReferenceException in GrainTimer (#6043)

### [1.5.10] (changes since 1.5.9)

- Non-breaking bug fixes
  - Remove activation from message target list if constructor threw an exception (#5960)

### [2.4.3] (changes since 2.4.2)

- Non-breaking improvements
  - Add "UseSiloUnobservedExceptionsHandler" extensions to the ISiloBuilder (#59120)

- Non-breaking bug fixes
  - Remove activation from message target list if constructor threw an exception (#5958)
  - Fix Connect blocked when ConnectAsync completed synchronously (#5963)
  - Stateless worker local compatibility check (#5917)
  - Fixed wrong condition for getting logContext (#5999)
  - Fix UTF8 encoding settings that appear to break execution of tests. (#6001)
  - Use MemFree when MemAvailable is not present (#6005)
  - Specify DateTimeKind.Utc when constructing DateTime instances (#6020)

### [3.0.0-rc1] (changes since 3.0.0-beta1)

- Non-breaking improvements
  - Remove unused "SetupSqlScriptFileNames" , It will cause the test to fail (#5872)
  - Improve codegen's .NET Core 3 compatibility 2 (#5883)
  - Improve graceful deactivation of grains performing transaction work (#5887)
  - Add "UseSiloUnobservedExceptionsHandler" extensions to the ISiloBuilder (#59120)
  - Add hard limits for message header and body size (#5911)
  - Memory usage for activation data improved. (#5907)
  - Stream configuration namespace cleanup. (#5923)
  - Lease based queue balancer refactor. (#5914)
  - Add detail to SiloHealthMonitor logs for superseded result (#5892)
  - ClusterHealthMonitor: ignore superseded probes (#5930)
  - Deny connections to known-dead silos (#5889)
  - Set Socket.NoDelay = true by default (#5934)
  - Adds a large sample that runs and tests locally in reliable configuration (#5909, #5953, #5951, #5955, #5984)
  - Migrate to ASP.NET "Bedrock" abstractions (#5935)
  - Remove AWS, Service Fabric, & ADO.NET metapackages (#5946)
  - Improves queries by adding lock hinting to membership protocol (#5954)
  - Bound connection attempt concurrency in ConnectionManager (#5894)
  - Cleanup Response class & improve ToString (#5975)
  - Fix connection log scoping (#5976)
  - Make CollectionAgeLimitAttribute easy to use! (#5961)
  - Remove unused IMembershipOracle interface (#5987)
  - Move FileLoggerProvider from Core to TestingHost (#5992)
  - Add additional internal health checks for membership (#5988)
  - Add serializer for RegexStreamNamespacePredicate (#5989)
  - Remove most instances of MarshalByRefObject (#5994)
  - Make TestClusterBuilder.AddSiloBuilderConfigurator and TestClusterBuilder.AddClientBuilderConfigurator fluent style APIs. (#5995)
  - Add IBinaryTokenStreamReader.Length property (#5997)
  - Remove InternalsVisibleTo set for extensions by making necessary internal types public (#6000)
  - Propagate message [de]serialization exceptions to callers (#5998)
  - Improve MethodInfo resolution for grain call filters (#6004)
  - Improve List<T>/ReadOnlyCollection<T> deep copy performance (#6010)
  - Cancel pending silo monitoring probe after ProbeTimeout elapses (#6006)
  - Simplify ConnectionListener.RunAsync (#6014)
  - Support adding [DebuggerStepThrough] to generated classes via project option (#6017)
  - Move from WindowsAzure.Storage library to Microsoft.AzureCosmos.Table and Microsoft.Azure.Storage.* packages. (#6013)
  - Update dependecies to their latest versions (#6025, #5983, #5943, #5973, #5945, #5944)

- Non-breaking bug fixes
  - Protect ClientState.PendingToSend with lock (#5881)
  - Fix NullReferenceException in AQStreamsBatchingTests.Dispose (#5888)
  - Stateless worker local compatibility check (#5917)
  - Remove activation from message target list if constructor threw an exception (#5958)
  - Clear RequestContext when spawning connections (#5974)
  - Fix potential deadlock with Connection.closeRegistration (#5986)
  - Fixed wrong condition for getting logContext (#5999)
  - Use MemFree when MemAvailable is not present (#6005)
  - Avoid generating duplicate method id switch labels (#6007)
  - CodeGen: disambiguate parameters with duplicate names (#6016)
  - Specify DateTimeKind.Utc when constructing DateTime instances (#6020)
  - Use half-duplex connections when accepting a connection from a pre-v3 silo (#6023)

### [1.5.9] (changes since 1.5.8)

- Non-breaking bug fixes
  - Do not call release header/body on a message in the dispatcher (#5921)

### [2.4.2] (changes since 2.4.1)

- Non-breaking improvements
  - Close connection on serialization error, to avoid data corruption from client. (#5899)
  - Add details to grain invocation exception logs (#5895)
  - Add hard limits for message header and body size (#5908)
  - Cleanup Message constructors & Headers assignment (#5902)
  - Remove SAEA pooling (#5915)
  - Fix default value for MaxMessageHeaderSize and MaxMessageBodySize (#5916)
  - Improve graceful deactivation of grains performing transaction work (#5887) (#5897)
  - When deserializling headers, check that we consumed all bytes (#5910)

- Non-breaking bug fixes
  - Fix header deserialization error handling (#5901)
  - Do not call release header/body on a message in the dispatcher (#5920)

### [3.0.0-beta1] (changes since 2.3.0)

- Non-breaking improvements
  - Introduced general component configurator pattern. (#5437)
  - Linux version of IHostEnvironmentStatistics (#5423)
  - Grain extensions are now available on system targets and Grain services (#5445)
  - Added IQueueData adapter for persistent streams. (#5450)
  - Add Incoming grain call filter extensions for ISiloBuilder (#5466)
  - Improve serializer performance hygiene (#5409)
  - Add UseLinuxEnvironmentStatistics method for ISiloBuilder (#5498)
  - Improve activation & directory convergence (#5424)
  - Updated stream subscription handle extension functions to handle batch consumption, complerable to what is supported for subscribe. (#5502)
  - Add square bracket guards (#5521)
  - Enable TransactionalStateStorageTestRunner to test with custom type (#5514)
  - Modified component configurator extension functions so order of configuration no longer matters. (#5458)
  - Fix #5519: use local silo as default primary silo (#5522)
  - Added batch stream production back in. (#5503)
  - Cleanup pass of named service configurator (#5528, #5535)
  - Dropped fluent support for Named Service Configurator (#5539)
  - Accommodate existing RequestContext.PropagateActivityId value in ClusterClient (#5575)
  - Fix packaging warning in Orleans.CodeGenerator.MSBuild (#5583)
  - Provide separate options for worker & IO pool min thread counts (#5584)
  - Implement IApplicationLifetime for ClientBuilder/SiloHostBuilder (#5586)
  - Add Analyzers to Orleans (#5589)
  - Improve Roslyn TypeCode generation (#5604)
  - Update xUnit & fix minor test project issues (#5598)
  - Remove lock from CallbackData (#5595)
  - Execute tasks scheduled against defunct activations (#5588)
  - Improve cleanup of activations on dead silos (#5646)
  - Fixes #5661 by allowing configuration to pass in value of MetadataPro… (#5662)
  - Make transaction log group max size configurable (#5656)
  - Avoid wrapping exceptions thrown during lifecycle (#5665)
  - Reduce default liveness probe timeout from 10 seconds to 5. (#5673)
  - Reduce delay localdirectory when cluster membership is not stable (#5677)
  - Create GrainReferenceKeyInfo (#5619)
  - Add codegen error for non-awaitable grain interface methods (#5530)
  - CodeGenerator: skip empty projects (#5689)
  - Expose versioning from membership (#5695)
  - Add UseAzureTableReminderService OptionsBuilder overload (#5703)
  - Remove ExpectedClusterSize & add MaxOperationBackoffTime (#5702)
  - Start MembershipTableCleanupAgent in Active instead of RuntimeGrainServices (#5722)
  - Start ClusterHealthMonitor in Active instead of BecomeActive (#5723)
  - Grain-based reminders: separate IReminderTable & IReminderTableGrain (#5714)
  - Dispose TestCluster after tests (#5715)
  - Check if debugger is attached before break (#5730)
  - Add validator for DevelopmentClusterMembershipOptions (#5721)
  - Improve lifecycle logging (#5711)
  - Minor client/silo teardown tweaks (#5712)
  - Use nameof instead of magic string (#5735)
  - Configure application parts in UseTransactions (#5741)
  - Add core tracing events (#5691)
  - HostedClient - use a slim IClusterClient implementation (#5745)
  - Improvements for cluster membership (#5747)
  - Make PlacementStrategy marker classes public
  - Changes to Orleans runtime to enable building Indexing as a NuGet package (#5674)
  - Added better type handling to DynamoDB deserialization (#5764)
  - Networking stack rewrite (#5436)
  - Remove message resend support (#5770)
  - Implement full-duplex silo-to-silo connections (#5776)
  - Add UsePerfCounterEnvironmentStatistics overload for ISiloBuilder (#5784)
  - Remove OrleansAzureUtils project and package (#5792)
  - Send a snapshot of the membership table on gossip (#5796)
  - Allow configuring outbound connection count & connection retry delay (#5798)
  - Improve codegen's .NET Core 3 compatibility (#5799)
  - Support configurable supported roles in transactional state. (#5802)
  - Fix message header serialization to align with 2.x (#5803)
  - Gossip status change on shutdown for SystemTargetBasedMembershipTable (#5804)
  - Introduce support for network protocol versioning (#5807)
  - Ignore superseded probe results (#5806)
  - Log a warning when blocking application messages in MessageCenter (#5814)
  - Wait before aborting connections to defunct silos (#5810)
  - Check that a silo is not known to be dead before attempting a connection (#5811)
  - Stop background transaction processing when a grain deactivates (#5832)
  - Reject failed activations and fix possible race condition (#5825)
  - Use simple await in Connection (#5831)
  - Use simple await in HostedClient.RunClientMessagePump (#5830)
  - Always log stack trace when a Task is enqueued for an invalid activation (#5833)
  - GatewayManager: return all gateways if all are marked dead (#5813)
  - Remove generics from grain directory caching (#5836)
  - Refactor EventHubDataAdapter to be plugable (#5580)
  - Change connection attempt failure timestamp (#5861)

- Non-breaking bug fixes
  - Fix catch condition (#5455)
  - Fix DI scope issue in azure blob (#5545)
  - On the client, close gateway connection to dead silos (#5561)
  - Prevent NullReferenceException with some storage providers when state is Nullable<T> (#5570
  - Fix #5565 - NullReferenceException in ConvertAsync helper (#5582)
  - Allow default(ImmutableArray<T>) to be serialized (#5587)
  - Fix NullReferenceException in TestCluster.cs (#5592)
  - NoOp delete when ETag is null in AzureTableStorage provider (#5577)
  - Fix potential NullReferenceException in PersistentStreamProvider (#5597)
  - Fix breakage in Microsoft.Extensions.Hosting (#5610)
  - Fix ReadLineStartingWithAsync for LinuxEnvironmentStatistics (#5608)
  - Add null check in MessageCenter.TryDeliverToProxy (#5641)
  - Fix Nullable<T> (#5663)
  - Fix Transactions test (#5615)
  - Fix #5473 - codegen fails on recursively defined types (#5688)
  - ClusterClient: only call IRuntimeClient.Reset for OutsideRuntimeClient (#5694)
  - Fix the test trace file name on Unix systems (#5708)
  - Fixed reminder issue. (#5739)
  - Fix incorrectly configured listening ports in tests (#5751)
  - Fix concurrency bug in TestCluster (#5754)
  - Replace Environment.FailFast with Environment.Exit (#5759)
  - Fix OnCompleteAsync & OnErrorAsync in StreamImpl. (#5769)
  - Fix ValidateInitialConnectivity bug (#5766)
  - Fix #5686 - Json serialization with Postgres (#5763)
  - Fix exception in LatestVersionSelector when there are no deployed versions of a grain. (#5720)
  - Fix potential NullReferenceException in CallbackData (#5777)
  - Fix build on VS 2019 16.2.0 (#5791)
  - Fix connection preamble process (#5790)
  - Fixed bug in SMS streams where events were not being delivered to batch observers. (#5801)
  - Remove LocalSilo from MembershipTableSnapshot. Detect death in gossip (#5800)
  - Fixed bug preventing OnError from being called on batch consumers. (#5812)
  - Do not mark disconnected gateways as dead (#5817)
  - Terminate ConfirmationWorker loop on deactivation (#5821)
  - Call ProcessTableUpdate before GossipToOthers (#5842)
  - Added ClientMessagingOptions.LocalAddress to ignore ConfigUtilities.GetLocalIPAddress that automatic pickups network interfaces. (#5838)
  - Fixes packaging of analyzers (#5845)
  - Fix potential deadlock between Catalog and LocalGrainDirectory (#5844)
  - Log options on silo and client startup (#5859)
  - Handle the case where the clustering provider does not support TableVersion (#5863)

### [2.4.1] (changes since 2.4.0)

- Non-breaking improvements
  - Added ClientMessagingOptions.LocalAddress to ignore ConfigUtilities.GetLocalIPAddress that automatic pickups network interfaces. (#5838)
  - Handle the case where the clustering provider does not support TableVersion (#5863)

- Non-breaking bug fixes
  - Call ProcessTableUpdate before GossipToOthers (#5842)
  - Fix potential deadlock between Catalog and LocalGrainDirectory (#5844)
  - Log options on silo and client startup (#5859)

### [2.4.0] (changes since 2.3.0)

- Non-breaking improvements
  - Improve serializer performance hygiene (#5409)
  - Add UseLinuxEnvironmentStatistics method for ISiloBuilder (#5498)
  - Improve activation & directory convergence (#5424)
  - Updated stream subscription handle extension functions to handle batch consumption, comparable to what is supported for subscribe. (#5502)
  - Add square bracket guards (#5521)
  - Added batch stream production back in. (#5503)
  - Allow default(ImmutableArray<T>) to be serialized (#5587)
  - Improve Roslyn TypeCode generation (#5604)
  - Remove lock from CallbackData (#5595)
  - Execute tasks scheduled against defunct activations (#5588)
  - Make transaction log group max size configurable (#5656)
  - Reduce delay local directory when cluster membership is not stable (#5677)
  - Add Incoming grain call filter extensions for ISiloBuilder (#5466)
  - ClusterClient: only call IRuntimeClient.Reset for OutsideRuntimeClient (#5694)
  - CodeGenerator: skip empty projects (#5689)
  - Add UseAzureTableReminderService OptionsBuilder overload (#5703)
  - Dispose TestCluster after tests (#5715)
  - Add validator for DevelopmentClusterMembershipOptions (#5721)
  - Fix CategoryDiscoverer first-chance exception while debugging (#5710)
  - Configure application parts in UseTransactions (#5741)
  - Added better type handling to DynamoDB deserialization (#5764)
  - Add UsePerfCounterEnvironmentStatistics overload for ISiloBuilder (#5784)
  - Improve codegen's .NET Core 3 compatibility (#5799)
  - Support configurable supported roles in transactional state. (#5802)
  - Cleanup transaction confirmation worker logging. (#5815)
  - Update xUnit & fix minor test project issues (#5598)
  - Avoid wrapping exceptions thrown during lifecycle (#5665)
  - Expose versioning from membership (#5695)
  - Improve lifecycle logging (#5711)
  - Improvements for cluster membership (#5747)
  - Send a snapshot of the membership table on gossip (#5796)
  - Always log stack trace when a Task is enqueued for an invalid activation (#5833)
  - Add core tracing events (#5691)

- Non-breaking bug fixes
  - Use local silo as default primary silo (#5522)
  - On the client, close gateway connection to dead silos (#5561)
  - Prevent NullReferenceException with some storage providers when state is Nullable<T> (#5570)
  - Accommodate existing RequestContext.PropagateActivityId value in ClusterClient (#5575)
  - Fix #5565 - NullReferenceException in ConvertAsync helper (#5582)
  - Fix NullReferenceException in TestCluster.cs (#5592)
  - NoOp delete when ETag is null in AzureTableStorage provider (#5577)
  - Fix potential NullReferenceException in PersistentStreamProvider (#5597)
  - Fix breakage in Microsoft.Extensions.Hosting (#5610)
  - Fix ReadLineStartingWithAsync for LinuxEnvironmentStatistics (#5608)
  - Add null check in MessageCenter.TryDeliverToProxy (#5641)
  - Improve cleanup of activations on dead silos (#5646)
  - Fix Nullable<T> (#5663)
  - Fixes #5661 by allowing configuration to pass in value of MetadataPro… (#5662)
  - Fix #5473 - codegen fails on recursively defined types (#5688)
  - Fix the test trace file name on Unix systems (#5708)
  - Fixed DynamoDB reminder issue (#5739)
  - Fix incorrectly configured listening ports in tests (#5751)
  - Fix concurrency bug in TestCluster (#5754)
  - Replace Environment.FailFast with Environment.Exit (#5759)
  - Fix OnCompleteAsync & OnErrorAsync in StreamImpl. (#5769)
  - Fix exception in LatestVersionSelector when there are no deployed versions of a grain. (#5720)
  - Fix build on VS 2019 16.2.0 (#5791)
  - Fixed bug in SMS streams where events were not being delivered to batch observers. (#5801)
  - Fixed bug preventing OnError from being called on batch consumers. (#5812)
  - Terminate ConfirmationWorker loop on deactivation (#5821)
  - Stop background transaction processing when a grain deactivates (#5832)
  - Reject failed activations and fix possible race condition (#5825)

### [2.3.6] (changes since 2.3.5)

- Non-breaking improvements
  - ClusterClient: only call IRuntimeClient.Reset for OutsideRuntimeClient (#5694)
  - CodeGenerator: skip empty projects (#5689)
  - Add UseAzureTableReminderService OptionsBuilder overload (#5703)
  - Dispose TestCluster after tests (#5715)
  - Add validator for DevelopmentClusterMembershipOptions (#5721)
  - Fix CategoryDiscoverer first-chance exception while debugging (#5710)
  - Configure application parts in UseTransactions (#5741)
  - Added better type handling to DynamoDB deserialization (#5764)

- Non-breaking bug fixes
  - Fix the test trace file name on Unix systems (#5708)
  - Fixed DynamoDB reminder issue (#5739)
  - Fix incorrectly configured listening ports in tests (#5751)
  - Fix concurrency bug in TestCluster (#5754)
  - Replace Environment.FailFast with Environment.Exit (#5759)
  - Fix OnCompleteAsync & OnErrorAsync in StreamImpl. (#5769)
  - Fix exception in LatestVersionSelector when there are no deployed versions of a grain. (#5720)

### [2.3.5] (changes since 2.3.4)

- Non-breaking improvements
  - Make transaction log group max size configurable (#5656)
  - Reduce delay local directory when cluster membership is not stable (#5677)
  - Add Incoming grain call filter extensions for ISiloBuilder (#5466)

- Non-breaking bug fixes
  - Fixes #5661 by allowing configuration to pass in value of MetadataPro… (#5662)
  - Fix #5473 - codegen fails on recursively defined types (#5688)

### [2.3.4] (changes since 2.3.3)

- Non-breaking bug fixes
  - Fix Nullable<T> (#5663)

### [2.3.3] (changes since 2.3.2)

- Non-breaking improvements
  - Allow default(ImmutableArray<T>) to be serialized (#5587)
  - Improve Roslyn TypeCode generation (#5604)
  - Remove lock from CallbackData (#5595)
  - Execute tasks scheduled against defunct activations (#5588)

- Non-breaking bug fixes
  - Fix #5565 - NullReferenceException in ConvertAsync helper (#5582)
  - Fix NullReferenceException in TestCluster.cs (#5592)
  - NoOp delete when ETag is null in AzureTableStorage provider (#5577)
  - Fix potential NullReferenceException in PersistentStreamProvider (#5597)
  - Fix breakage in Microsoft.Extensions.Hosting (#5610)
  - Fix ReadLineStartingWithAsync for LinuxEnvironmentStatistics (#5608)
  - Add null check in MessageCenter.TryDeliverToProxy (#5641)
  - Improve cleanup of activations on dead silos (#5646)

### [2.3.2] (changes since 2.3.1)

- Non-breaking bug fixes
  - On the client, close gateway connection to dead silos (#5561)
  - Prevent NullReferenceException with some storage providers when state is Nullable<T> (#5570)
  - Accommodate existing RequestContext.PropagateActivityId value in ClusterClient (#5575)
  
### [2.3.1] (changes since 2.3.0)

- Non-breaking improvements
  - Improve serializer performance hygiene (#5409)
  - Add UseLinuxEnvironmentStatistics method for ISiloBuilder (#5498)
  - Improve activation & directory convergence (#5424)
  - Updated stream subscription handle extension functions to handle batch consumption, comparable to what is supported for subscribe. (#5502)
  - Add square bracket guards (#5521)
  - Added batch stream production back in. (#5503)

- Non-breaking bug fixes
  - Use local silo as default primary silo (#5522)

### [2.3.0] (changes since 2.2.0)

- Breaking changes
  - No changes breaking the wire protocol or persisted state that would break a rolling upgrade
  - Migration to Microsoft.Extensions.Options 2.1.1 (#5385) requires a namespace change in the code
  - `Refactored stream batch behaviors to support batch consumption.` (#5425) technically a breaking change due to the changes to the batch streaming API. However, it shouldn't break any working application code because the batching functionality wasn't fully wired previously.
  This release is backward-compatible with 2.x releases.

- Non-breaking improvements
  - Added 'First' and 'Last' to grain lifecycle stages. (#5248)
  - Avoid emitting assembly-level GeneratedCodeAttribute (#5270)
  - Use alias-qualified name in GetBindingFlagsParenthesizedExpressionSyntax (#5269)
  - Don't allow read only transaction participants to be selected as the manager (#5267)
  - Add configurable timeout to wait for queued messages being forwarded (#5268)
  - Sort out LocalGrainDirectory shutdown sequence (#5276)
  - Add CollectionAge validation to GrainCollectionOptions (#5290)
  - Optimize memory allocation with custom EqualityComparer (#5210)
  - Always Interleave modified to also be interleavable. (#5344)
  - Batching batch containers pulling agent retrieves from cache (#5336)
  - Invalidate activation cache entries from old epochs (#5352)
  - Change usages of TypeInfo back to Type (#5338)
  - Microsoft.Extensions.Hosting support (#5261, #5355)
  - Persistent state facet (#5373)
  - Updates XML documentation to call out prereq of `LoadShedding`. (#5387)
  - Enable tx test kit pkg (#5380)
  - Update to Microsoft.Extensions.Options 2.1.1 (#5385)
  - Mark key legacy types/methods as [Obsolete] (#5239)
  - Upgrade to EventHub 2.2.1 (#5384)
  - Mark ILBasedSerializer as obsolete (#5396)
  - Add event on gateway count changed (#5133)
  - Enable HostedClient by default (#5395)
  - Enable "cleaning" of all dead entries in the membership table (#5389, #5455)
  - Remove delegate allocation from interner (#5410)
  - Remove response callback using a single operation (#5406)
  - Throw during startup if no grain classes/interfaces in app parts (#5413)
  - Fix OneWay cache invalidation (#5401)
  - Adds a LoadSheddingValidator class (#5400, #5416)
  - Linux version of IHostEnvironmentStatistics (#5423)

- Non-breaking bug fixes
  - Fix invalid comparison in TransactionAgent (#5289)
  - Fix package dependency condition for Microsoft.Orleans.Transactions. (#5307)
  - Fix defensive check in LogConsistentGrain (#5319)
  - Fix package versioning in csproj files (#5333)
  - Fix #5342: Incorrect specification of global alias (#5343)
  - Add handling when pulling agent fails RegisterAsProducer to pubsub (#5354)
  - Use grain state type when deserializing json state in azure table storage (#4994)
  - Pulling agent losing subscriptions fix (#5372)
  - Fix #5398: AmbiguousMatchException in code generator (#5407)


### [2.3.0-rc2] (changes since 2.3.0-rc1)

`Refactored stream batch behaviors to support batch consumption.` (#5425) is the only change. While technically it is breaking due to the changes to the batch streaming API, it shouldn't break any working application code because the batching functionality wasn't fully wired previously. No breaking change in the wire protocol or persistence. This release is backward-compatible with 2.x releases.

### [2.3.0-rc1] (changes since 2.2.0)

- Breaking changes
  - No changes breaking the wire protocol or persisted state that would break a rolling upgrade
  - Migration to Microsoft.Extensions.Options 2.1.1 (#5385) requires a namespace change in the code

- Non-breaking improvements
  - Added 'First' and 'Last' to grain lifecycle stages. (#5248)
  - Avoid emitting assembly-level GeneratedCodeAttribute (#5270)
  - Use alias-qualified name in GetBindingFlagsParenthesizedExpressionSyntax (#5269)
  - Don't allow read only transaction participants to be selected as the manager (#5267)
  - Add configurable timeout to wait for queued messages being forwarded (#5268)
  - Sort out LocalGrainDirectory shutdown sequence (#5276)
  - Add CollectionAge validation to GrainCollectionOptions (#5290)
  - Optimize memory allocation with custom EqualityComparer (#5210)
  - Always Interleave modified to also be interleavable. (#5344)
  - Batching batch containers pulling agent retrieves from cache (#5336)
  - Invalidate activation cache entries from old epochs (#5352)
  - Change usages of TypeInfo back to Type (#5338)
  - Microsoft.Extensions.Hosting support (#5261, #5355)
  - Persistent state facet (#5373)
  - Updates XML documentation to call out prereq of `LoadShedding`. (#5387)
  - Enable tx test kit pkg (#5380)
  - Update to Microsoft.Extensions.Options 2.1.1 (#5385)
  - Mark key legacy types/methods as [Obsolete] (#5239)
  - Upgrade to EventHub 2.2.1 (#5384)
  - Mark ILBasedSerializer as obsolete (#5396)
  - Add event on gateway count changed (#5133)
  - Enable HostedClient by default (#5395)
  - Enable "cleaning" of all dead entries in the membership table (#5389)
  - Remove delegate allocation from interner (#5410)
  - Remove response callback using a single operation (#5406)
  - Throw during startup if no grain classes/interfaces in app parts (#5413)
  - Fix OneWay cache invalidation (#5401)
  - Adds a LoadSheddingValidator class (#5400)

- Non-breaking bug fixes
  - Fix invalid comparison in TransactionAgent (#5289)
  - Fix package dependency condition for Microsoft.Orleans.Transactions. (#5307)
  - Fix defensive check in LogConsistentGrain (#5319)
  - Fix package versioning in csproj files (#5333)
  - Fix #5342: Incorrect specification of global alias (#5343)
  - Add handling when pulling agent fails RegisterAsProducer to pubsub (#5354)
  - Use grain state type when deserializing json state in azure table storage (#4994)
  - Pulling agent losing subscriptions fix (#5372)
  - Fix #5398: AmbiguousMatchException in code generator (#5407)

### [1.5.7] (changes since 1.5.6)

Two fixes backported from v2.x
- Non-breaking bug fixes
  - Fixes for Multi-Cluster Support (#3974)
  - Add GSI cache maintenance and tests (#5184)

### [2.2.4] (changes since 2.2.3)

- Non-breaking improvements
  - Add CollectionAge validation to GrainCollectionOptions (#5290)
  - Fix package versioning in csproj files (#5333)
  - Invalidate activation cache entries from old epochs (#5352)
  - Use grain state type when deserializing json state in azure table storage (#4994)
  - Updates XML documentation to call out prereq of `LoadShedding`. (#5387)

- Non-breaking bug fixes
  - Fix defensive check in LogConsistentGrain (#5319)
  - Fix #5342: Incorrect specification of global alias (#5343)
  - Add handling when pulling agent fails RegisterAsProducer to pubsub (#5354)
  - Pulling agent losing subscriptions fix (#5372)

### [2.2.3] (changes since 2.2.2)

- Non-breaking improvements
  - Avoid emitting assembly-level GeneratedCodeAttribute (#5270)
  - Add configurable timeout to wait for queued messages being forwarded (#5268)
  - Sort out LocalGrainDirectory shutdown sequence (#5276)

- Non-breaking bug fixes
  - Use alias-qualified name in GetBindingFlagsParenthesizedExpressionSyntax (#5269)

### [2.2.2] (changes since 2.2.1)

- Non-breaking bug fixes
  - Fix package dependency condition for Microsoft.Orleans.Transactions. (#5307)

### [2.2.1] (changes since 2.2.0)

- Breaking changes
  - None

- Non-breaking bug fixes
  - Don't allow read only transaction participants to be selected as the manager (#5267)
  - Fix invalid comparison in TransactionAgent (#5289)

### [2.2.0] (changes since 2.1.0)

- Breaking changes
  - None

- Non-breaking improvements
  - Avoid lazy initialization when disposing OutboundMessageQueue (#5049)
  - CodeGen: Fix race in Orleans.sln build (#5035)
  - Change Orleans.TelemetryConsumers.NewRelic to target .NET Standard (#5044) (#5047)
  - Typo and spelling check XML doc and strings. A to E. #Hacktoberfest (#5051, #5055, #5060, #5065)
  - Filter static types from list of types known to serializer (#5036)
  - fixed HostedClient method name in exception text (#5057)
  - Adding global alias for binding flags in generator (#5068)
  - Allow placement strategies to skip directory registration (#5074)
  - CodeGen: Warn users when a type inherits from a type defined in a reference assembly (#5031)
  - IMessageCenter.WaitMessage support cancellation (#5072)
  - Allow placement strategies to specify deterministic activation ids (#5082)
  - Add Orleans.Transaction.Testkit project structure (#5103)
  - Internal transactional states are now immutable (#5149)
  - Log warning when ClusterMembershipOptions.ValidateInitialConnectivity=true (#5148)
  - Start using Span and new language features for increasing Orleans perfomance (#5061)
  - Lock worker error handling improvements (#5175)
  - Add TimerManager as Task.Delay replacement (#5201)
  - Cleanup Transaction Agent (#5188)
  - Replace Task.RunSynchronously usage with alternative (#5204)
  - fix multicluster registration test (#5186)
  - Fix AzureSilo startup (#5213)
  - UniqueKey serialization optimizations (#5193)
  - Expedite TypeManager refresh upon cluster membership change  (#5233)
  - Ensure OrleansProviders is added as an ApplicationPart in streams providers (#5234)
  - Update ZooKeeperNetEx package to 3.4.12.1 (#5236)
  - Include exception in TryForwardRequest info log (#5238)
  - Improve logging of stream delivery errors. (#5230)

- Non-breaking bug fixes
  - Resolve transaction on abort. (#4996)
  - Avoid modification of interned SiloAddresses in Consul and ZooKeeper gateway providers (#5054)
  - Partial fix for transaction recovery tests (#5070)
  - Revert #4382 (#5086, #5088)
  - Fixed bug in transaction confirmation logic (#5098)
  - Fix rootKvFolder is not backward compatible (#5100)
  - Fix test cluster deploy deadlock (#5167)
  - Fix drain logic in ThreadPoolExecutor (#5208)
  - Don't throw SiloUnavailableException when a gateway stops (#5209)
  - Fix call chain reentrancy (#5145)
  - Support ProxyGatewayEndpoint from legacy configuration (#5214)
  - Add GSI cache maintentance and tests (#5184)

### [2.2.0-rc1] (changes since 2.2.0-beta1)

- Breaking changes
  - None

- Non-breaking improvements
  - Internal transactional states are now immutable (#5149)
  - Log warning when ClusterMembershipOptions.ValidateInitialConnectivity=true (#5148)
  - Start using Span and new language features for increasing Orleans perfomance (#5061)
  - Lock worker error handling improvements (#5175)
  - Add TimerManager as Task.Delay replacement (#5201)
  - Cleanup Transaction Agent (#5188)
  - Replace Task.RunSynchronously usage with alternative (#5204)
  - fix multicluster registration test (#5186)

- Non-breaking bug fixes
  - Fix rootKvFolder is not backward compatible (#5100)
  - Fix test cluster deploy deadlock (#5167)
  - Fix drain logic in ThreadPoolExecutor (#5208)
  - Don't throw SiloUnavailableException when a gateway stops (#5209)
  - Fix call chain reentrancy (#5145)

### [2.2.0-beta1] (changes since 2.1.0)

- Breaking changes
  - None

- Non-breaking improvements
  - Avoid lazy initialization when disposing OutboundMessageQueue (#5049)
  - CodeGen: Fix race in Orleans.sln build (#5035)
  - Change Orleans.TelemetryConsumers.NewRelic to target .NET Standard (#5044) (#5047)
  - Typo and spelling check XML doc and strings. A to E. #Hacktoberfest (#5051, #5055, #5060, #5065)
  - Filter static types from list of types known to serializer (#5036)
  - fixed HostedClient method name in exception text (#5057)
  - Adding global alias for binding flags in generator (#5068)
  - Allow placement strategies to skip directory registration (#5074)
  - CodeGen: Warn users when a type inherits from a type defined in a reference assembly (#5031)
  - IMessageCenter.WaitMessage support cancellation (#5072)
  - Allow placement strategies to specify deterministic activation ids (#5082)
  - Add Orleans.Transaction.Testkit project structure (#5103)

- Non-breaking bug fixes
  - Resolve transaction on abort. (#4996)
  - Avoid modification of interned SiloAddresses in Consul and ZooKeeper gateway providers (#5054)
  - Partial fix for transaction recovery tests (#5070)
  - Revert #4382 (#5086, #5088)
  - Fixed bug in transaction confirmation logic (#5098)

### [2.1.2] (changes since 2.1.1)

- Non-breaking bug fixes
  - Revert "Don't enforce reentrancy for one way requests" #4382 (#5086). Fixed a regression that could in certain cases lead to a violation of non-reentrant execution of a grain.

### [2.1.1] (changes since 2.1.0)

- Non-breaking bug fixes
  - Avoid modification of interned SiloAddresses in Consul and ZooKeeper providers #5054 

### [2.1.0] (changes since 2.0.0)

- Major changes
  - New scheduler (#3792)
  - Hosted Client (#3362)
  - Distributed Transaction Manager (#3820, #4502, #4538, #4566, #4568, #4591, #4599, #4613, #4609, #4616, #4608, #4628, #4638, #4685, #4714, #4739, #4768, #4799, #4781, #4810, #4820, #4838, #4831, #4871, #4887)
  - New Code Generator (#4934, #5010, #5011)
  - Support for Tansfer of Coordination in transaction (#4860, #4894, #4949, #5026, #5024)
  
- Breaking changes
  - None

- Non-breaking improvements
  - Test clustering: minor fixups (#4342)
  - TestCluster: wait for cluster stabilization before starting tests (#4343)
  - Avoid continuation in synchronous case (#4422)
  - Improve Dictionary allocation in RequestContext (#4435)
  - Copy elements in-place in InvokeMethodAsync (#4463)
  - Azure blob storage provider: respect UseJson setting (#4455)
  - Fix orleans integration with third party DI solution which requires public constructor (#4453)
  - Remove unused Stopwatch in Grain<T>.OnSetupState (#4403) (#4472)
  - Add validator for ClusterOptions (#4450)
  - Non-static statistics: Round 1 (#4515)
  - Remove saving of minidumps because that functionality is platform specific. (#4558)
  - Fix Dependency Injection without changing Abstractions project (#4573)
  - Sanitize "." from azure queue name (#4582)
  - Add Client/SiloHost builder delegate to legacy GrainClient and Silo/AzureSilo (#4552)
  - Support of ValueTask as a grain method return type (#4562)
  - Convert IMembershipTableGrain into a SystemTarget (#4479)
  - Convert counter values before calling ITelemetryProducer.TrackMetric (#4623)
  - Optimize removing consumed buffers from read buffer (#4629)
  - Remove unused settings MaxPendingWorkItemsHardLimit in SchedulingOptions (#4672)
  - Udpate reference links in sql files (#4684)
  - Use netcoreapp2.0 for msbuild target dll if using dotnet core msbuild but targeting full .net (#4689)
  - Make AzureBasedReminderTable public to allow reuse in extensions (#4699)
  - Remove per-call timer (#4399)
  - Make LifecycleSubject logging less verbose (#4660)
  - Do not use ip address from interface not operational (#4713)
  - Updated Ignore(this Task) method (#4729)
  - Make azure queue name configurable (#4762)
  - Auto-installing grain extensions (#4815)
  - Allow implicit subscription attribute to be inheritable (#4824)
  - Do not place stateless worker locally if the silo is stopping (#4853)
  - When deactivating a grain, do not stop timers if there are running requests  (#4830)
  - No default grains storage added to container if one is not configured. (#4861)
  - Revisit silo stop/shutdown timeout (#4875)
  - Add timeout mechanism for grain deactivation (#4883)
  - Do not try to register GrainVersionStore if an implementation of IVersionStore is already registered (#4911)
  - Consul clustering enhancements (#4942)
  - IsOrleansShallowCopyable fixes (#4945)
  - Feature per grain collection attribute (#4890)
  - Add Microsoft.Orleans.Streaming.AzureStorage as a dependency to Microsoft.Orleans.OrleansAzureUtils. (#4954)
  - Migrate Orleans.TelemetryConsumers.Counters to netstandard (#4914)
  - Add TableName to AzureStorageClusteringOptions, AzureStorageGatewayOptions and AzureTableReminderStorageOptions (#4978)
  - Added support for TableName on AWS legacy configurator (#4983)
  - Added Validations for Blob Names and refactored the AzureUtils for Blob & Container names. (#5020)
  - Add LargeMessageWarningThreshold back to Silo(Client)MessagingOptions (#5022)
  - Make MaxSockets in SocketManager configurable. (#5033)
  - Cleanup types in transaction state storage interface (#5030)

- Non-breaking bug fixes
  - Fix telemetry consumer construction (#4392)
  - Fix client connection retry (#4429)
  - Fix routing in Silo Gateway (#4483)
  - Don't generate serializers for foreign types in Orleans.Streaming.EventHubs (#4487)
  - Fix NRE on AWS DynamoDB storage provider. #4482 (#4513)
  - Fix Exception thrown in MembershipOracle.TryToSuspectOrKill (#4508)
  - Fix logging level check on Grain exception (#451
  - Assign Issue property in RecordedConnectionIssue.Record(...) (#4598)
  - Fix (or workaround?) for codegen using netcore/netstandard 2.1 (#4673)
  - Don't enforce reentrancy for one way requests (#4382)
  - Cleanup Reminders PartitionKey (#4749)
  - Fix NullReferenceException in ExecutingWorkItemsTracker (#4850)
  - Fix NullReferenceException in LocalGrainDirectory when trace logging is enabled (#4854)
  - Fix dependency injection cycle when OrleansJsonSerializer is used as a serialization provider (#4876)
  - Propagate unserializable exceptions to callers (#4907)
  - Fixing race condition with simple queue cache (#4936)
  - More fixed to Transfer of Coordination (transactions) (#4968)
  - Ensure AsyncAgent restarts on fault (#5023)

### [1.5.6] (changes since 1.5.5)

- Non-breaking improvements
  - Make MaxSockets in SocketManager configurable #5033

### [2.1.0-rc2] (changes since 2.1.0-rc1)

- Major changes
  - New Code Generator (#4934, #5010, #5011)

- Breaking changes
  - None

- Non-breaking bug fixes
  - More fixed to Transfer of Coordinatio (transactions) (#4968)

### [2.1.0-rc1] (changes since 2.1.0-beta1)

- Major changes
  - Transactions (beta2) (#4851, #4923, #4951, #4950, #4953)
  - Support for Tansfer of Coordination in transaction (#4860, #4894, #4949)

- Breaking changes
  - None

- Non-breaking improvements
  - Do not try to register GrainVersionStore if an implementation of IVersionStore is already registered (#4911)
  - Consul clustering enhancements (#4942)
  - IsOrleansShallowCopyable fixes (#4945)
  - Feature per grain collection attribute (#4890)
  - Add Microsoft.Orleans.Streaming.AzureStorage as a dependency to Microsoft.Orleans.OrleansAzureUtils. (#4954)
  - Migrate Orleans.TelemetryConsumers.Counters to netstandard (#4914)
  - Add TableName to AzureStorageClusteringOptions, AzureStorageGatewayOptions and AzureTableReminderStorageOptions (#4978)
  - Added support for TableName on AWS legacy configurator (#4983)

- Non-breaking bug fixes
  - Propagate unserializable exceptions to callers (#4907)
  - Fixing race condition with simple queue cache (#4936)

### [1.5.5] (changes since 1.5.4)

- Non-breaking bug fixes
  - Fix programmatic subscribe bugs (#4943 - #3843) 
  - Propagate message serialization errors to callers (#4944 - #4907)
- Breaking bug fixes
  - Add StreamSubscriptionHandleFactory to subscribe on behalf feature (#4943 - #3851). While technically a breaking change, it only impacts users of the programmatic subscriptions feature that tried to use it with SMS stream by fixing that scenario (along with #3843).

### [2.0.5]
- Non-breaking bug fixes
  - Use netcoreapp2.0 for msbuild target dll if using dotnet core msbuild but targeting full .net (#4895) 

### [2.1.0-beta1] (changes since 2.0.0)

- Major changes
  - New scheduler (#3792)
  - Hosted Client (#3362)
  - Distributed Transaction Manager (beta) (#3820, #4502, #4538, #4566, #4568, #4591, #4599, #4613, #4609, #4616, #4608, #4628, #4638, #4685, #4714, #4739, #4768, #4799, #4781, #4810, #4820, #4838, #4831, #4871, #4887)
  
- Breaking changes
  - None

- Non-breaking improvements
  - Test clustering: minor fixups (#4342)
  - TestCluster: wait for cluster stabilization before starting tests (#4343)
  - Avoid continuation in synchronous case (#4422)
  - Improve Dictionary allocation in RequestContext (#4435)
  - Copy elements in-place in InvokeMethodAsync (#4463)
  - Azure blob storage provider: respect UseJson setting (#4455)
  - Fix orleans integration with third party DI solution which requires public constructor (#4453)
  - Remove unused Stopwatch in Grain<T>.OnSetupState (#4403) (#4472)
  - Add validator for ClusterOptions (#4450)
  - Non-static statistics: Round 1 (#4515)
  - Remove saving of minidumps because that functionality is platform specific. (#4558)
  - Fix Dependency Injection without changing Abstractions project (#4573)
  - Sanitize "." from azure queue name (#4582)
  - Add Client/SiloHost builder delegate to legacy GrainClient and Silo/AzureSilo (#4552)
  - Support of ValueTask as a grain method return type (#4562)
  - Convert IMembershipTableGrain into a SystemTarget (#4479)
  - Convert counter values before calling ITelemetryProducer.TrackMetric (#4623)
  - Optimize removing consumed buffers from read buffer (#4629)
  - Remove unused settings MaxPendingWorkItemsHardLimit in SchedulingOptions (#4672)
  - Udpate reference links in sql files (#4684)
  - Use netcoreapp2.0 for msbuild target dll if using dotnet core msbuild but targeting full .net (#4689)
  - Make AzureBasedReminderTable public to allow reuse in extensions (#4699)
  - Remove per-call timer (#4399)
  - Make LifecycleSubject logging less verbose (#4660)
  - Do not use ip address from interface not operational (#4713)
  - Updated Ignore(this Task) method (#4729)
  - Make azure queue name configurable (#4762)
  - Auto-installing grain extensions (#4815)
  - Allow implicit subscription attribute to be inheritable (#4824)
  - Do not place stateless worker locally if the silo is stopping (#4853)
  - When deactivating a grain, do not stop timers if there are running requests  (#4830)
  - No default grains storage added to container if one is not configured. (#4861)
  - Revisit silo stop/shutdown timeout (#4875)
  - Add timeout mechanism for grain deactivation (#4883)

- Non-breaking bug fixes
  - Fix telemetry consumer construction (#4392)
  - Fix client connection retry (#4429)
  - Fix routing in Silo Gateway (#4483)
  - Don't generate serializers for foreign types in Orleans.Streaming.EventHubs (#4487)
  - Fix NRE on AWS DynamoDB storage provider. #4482 (#4513)
  - Fix Exception thrown in MembershipOracle.TryToSuspectOrKill (#4508)
  - Fix logging level check on Grain exception (#451
  - Assign Issue property in RecordedConnectionIssue.Record(...) (#4598)
  - Fix (or workaround?) for codegen using netcore/netstandard 2.1 (#4673)
  - Don't enforce reentrancy for one way requests (#4382)
  - Cleanup Reminders PartitionKey (#4749)
  - Fix NullReferenceException in ExecutingWorkItemsTracker (#4850)
  - Fix NullReferenceException in LocalGrainDirectory when trace logging is enabled (#4854)
  - Fix dependency injection cycle when OrleansJsonSerializer is used as a serialization provider (#4876)

### [2.0.4]

- Non-breaking bug fixes
  - Workaround for [CoreFx/#30781](https://github.com/dotnet/corefx/issues/30781) when running on .NET Core (#4736)
  - Fix for .NET Core 2.1 build-time code generation (#4673)

### [1.5.4] (changes since 1.5.3)

- Non-breaking bug fixes
  - Fixing codegen when a type's name contains comma (#3639)
  - Set a timeout value for the synchronous socket read operations (#3716)
- Non-breaking improvements
  - Add MaxSocketAge to OrleansConfiguration.xsd (#3721)
  - Avoid overload checks for response messages (#3743)
  - Expedite gateway retries when gateway list is exhausted (#3758)

### [2.0.3]

- Non-breaking improvements
  - Test clustering: minor fixups for logging and configuration (#4342)
  - TestCluster: wait for cluster stabilization before starting tests (#4343)
  - Avoid continuation in synchronous work item scheduling case (#4422)
  - Improve Dictionary allocation in `RequestContext` (#4435)
  - Copy elements in-place in `InvokeMethodAsync` (#4463)
  - Fix orleans integration with third party DI solution which requires public constructor (#4453, #4573)
  - Remove unused Stopwatch in `Grain<T>.OnSetupState` (#4472)
  - Increase diagnostic logging in code generator (#4481)
  - Clarify `UseDevelopmentClustering` and `UseLocalhostClustering` (#4438)

- Non-breaking bug fixes
  - Fix telemetry consumer construction (#4392)
  - Fix client connection retry (#4429)
  - Azure blob storage provider: respect UseJson setting (#4455)
  - Fix routing in Silo Gateway (#4483)
  - Don't generate serializers for foreign types in *Orleans.Streaming.EventHubs* (#4487)
  - Fix NRE on AWS DynamoDB storage provider (#4513)
  - Fix Exception thrown in `MembershipOracle.TryToSuspectOrKill` (#4508)
  - Fix logging level check on Grain exception (#4511)
  - Add validator for `ClusterOptions` (#4450)
  - Remove saving of minidumps because that functionality is platform specific. (#4558)

### [2.0.0] (changes since 2.0.0-rc2)

- Major changes
  - All included providers obtain ServiceId and ClusterId from the global ClusterOptions and do not have those properties on their own options classes (#4235, #4277, #4290)
  - Use string for ServiceId instead of Guid (#4262)

- Breaking changes
  - WithCodeGeneration: accept ILoggerFactory instead of ILogger (#4204)
  - Remove Service Fabric configuration helpers & update sample (#4234)
  - Disable packaging of Orleans.Clustering.ServiceFabric (#4259)
  - Stop packaging Microsoft.Orleans.OrleansGCPUtils (#4330)

- Non-breaking improvements
  - Add serialization methods to RulesViolationException (#4215)
  - CodeGen: only filter out generated types where the generator is Orleans (#4249)
  - Add simple retry functionality to IClusterClient.Connect(...) (#4161)
  - Grain call filters: add distinction between InterfaceMethod and ImplementationMethod (#4216)
  - Improves ADO.NET script finding by moving them to project directory (#4243)
  - protobuf-net serializer (#4170)
  - Improve startup time for localhost cluster. (#4245)
  - Client typemap refresh (#4257)
  - Conditionally include @(Compile) cache file (#4258)
  - Enable SystemTarget routing in the Gateway (#4254)
  - Add config option to override MessagingOptions.ResponseTimeout when the debugger is attached (#4193, #4307)
  - Remove [Obsolete] attributes from most legacy types (#4255)
  - Orleans.TestingHost: support for .NET Standard (#4223)
  - Add validation for pubsubstore when using persistent streams (#4273)
  - Clean up EH checkpointing (#4271)
  - Improve error message when attempting to use reminders without configuring a reminder table (#4287)
  - Ported log consistency providers from IProvider. (#4292)
  - Remove inconsistent sub builder pattern in streaming (#4289)
  - DynamoDB transaction log (#4056)

- Non-breaking bug fixes
  - PerfCounterEnvironmentStatistics never reports CpuUsage (#4219)
  - Fix ADO.NET Reminder configuration & re-enable tests (#4214)
  - Register SiloClusteringValidator later so other validator will be called before (#4211)
  - Fixes for Multi-Cluster Support (#3974)
  - Change SQL parameter casing for Turkish parameter binding support on ODP .Net Managed (#4246)
  - Fix race condition (#4269)
  - Lease based queue balancer fixes (#4267)
  - Fixed bug in custom storage log consistency provider factory. (#4323)

### [2.0.0-rc2] (changes since 2.0.0-rc1)

- Major changes
  - A new "facade" API for easier configuration of various aspects of stream providers: Persistent stream configurators (#4164)

- Breaking changes
  - Align IClientBuilder APIs with ISiloHostBuilder (#4079)
  - Rename MembershipOptions to ClusterMembershipOptions (#4145)
  - Normalize cluster config & simplify binding IConfiguration to TOptions (#4136)

- Non-breaking improvements
  - Improve usability of dev cluster (#4090)
  - Extensions should add their own application parts (#4091)
  - Moved IStartupTask to Runtime.Abstractions package. Address #4106 (#4108)
  - Improve configuration validators for ADO.NET configuration (#4097)
  - In ActivationCountPlacementDirector, place locally if the cache is not populated yet (#4130)
  - Improve usability of custom grain placement configuration (#4102)
  - Remove legacy configuration requirement from Service Fabric hosting (#4138)
  - Fix #4123: use List instead of IList in StaticGatewayListProviderOptions (#4147)
  - Support treating all descendants of a base class as [Serializable] (#4133)
  - Improve how grain services are registered (#4155)
  - Do not call ResolveIPAddress in EndpointOptions constructor (#4171)
  - When the silo shutdown, deactivate grain activations at an earlier stage (#4177)
  - Improved transparancy and timing of silo lifecycle. (#4175)
  - Set GrainService.Status to Started in the base implementation of StartInBackground(). (#4180)
  - Validate that a ClusterId has been specified (#4160)

- Non-breaking bug fixes
  - ADO.NET: Fix formatting of generic class names in storage provider (#4140)
  - Fix for PerfCounterEnvironmentStatistics never reports CpuUsage (#4148)
  - Fix silo startup (#4135)
  
### [2.0.0-rc1] (changes since 2.0.0-beta3)

- Major changes
  - New provider lifecycle model to replace the old one (#3738, #3887, #3946, #3927, #4000, #4026, #4022, #4045, #4031, #4047, #4063, #4042, #4064, #4066, #4067, )
  - Builder pattern and options-based configuration of components and extensions (#3897, #3900, #3878, #3901, #3947, #3972, #3977, #3948, #3963, #3981, #4020, #4025, #4024, #4030, #4035, #4029, #4022, #4031, #4049, #4064, #4066, #4070, #4067, #4074)

- Breaking changes
  - Allow reentrancy within a grain call chain (#3185, #3958). Enabled by default.
  - Move legacy logging methods to legacy package (#3808)
  - Rename "SQL" to "AdoNet" everywhere (#3990)
  - Move ObserverSubscriptionManager to legacy (#3999)
  - Remove metrics publishers (#3988)
  - Make most methods of Grain class non-virtual. (#4004)
  - Refactor EndpointOptions to allow listening on an address that is different from the externally reachable address  (#4005)
  - Remove statistics table publishers (#4023)
  - Add startup tasks to replace deprecated bootstrap providers (#4026)
  - Remove FastKillOnCancel setting, add ProcessExitHandlingOptions (#4036)
  - Configure default application parts if no assemblies have been added (#4058)

- Non-breaking improvements
  - Add processing time measure on silo start up sequence and code gen (#3788)
  - Make buffer size bounded when reading connection preamble (#3818)
  - Make UnObservedExceptionHandler optional (#3829)
  - Display GrainId/type of SystemGrain and SystemTarget in GrainId.ToStringImpl (#3849)
  - Disable debug context usage (#3861)
  - Allow for null access/secret for EC2 provisioned credentials. (#3870)
  - Bring back PerfCounterEnvironmentStatistics  (#3891)
  - Expose RequestContextData in PlacementTarget (#3899)
  - Mark all created threads as background & name all threads (#3902)
  - Remove Newtonsoft.JSON dependency from core abstractions (#3926)
  - Update Service Fabric to support .NET Standard (#3931)
  - Dispose InboundMessageQueue during MessageCenter disposal (#3938)
  - Removed uncessesary lock in LocalGrainDirectory (#3961)
  - Add exception msg to WithTimeout method. Refactor MembershipTableFactory (#3962)
  - Allow OrleansJsonSerializer to be used as an external serialization provider (#3960)
  - Sanitize azure queue name (#4001)
  - Outgoing grain call filters (#3842)

- Non-breaking bug fixes
  - Fix read lock on FileLogger & FileLogConsumer. and switch to UTF8 (#3856)
  - Return the Cluster GrainInterfaceMap instead of the local one in InsideRuntimeClient (#3875)
  - Remove locks in LoadedProviderTypeLoaders and ProviderTypeLoader that appear unnecessary and caused occasional deadlocks in tests. (#3914)
  - TestCluster: set instance number for new Silo handles (#3939)
  - Use UriBuilder in ToGatewayUri() (#3937)
  - Fix deadlocks in AdoNet provider and tests caused by AdoNet driver implementations (#3163)
  - Fix FastKillOnCancelKeyPress not stopping the process. (#3935)
  - Fix logger exceptions (#3916)
  - Sort list of silos in HashBasedPlacementDirector (#3964)
  - Fix leasebasedbalancer bug (#4072)

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

### [1.5.3] (changes since 1.5.2)

- Non-breaking improvements
  - CodeGen: support builds which use reference assemblies (#3753)

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
  - Reset client gateway receiver buffer on socket reset. #2316
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
  - Reset client gateway receiver buffer on socket reset. #2316
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
