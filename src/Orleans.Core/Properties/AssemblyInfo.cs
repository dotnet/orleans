using Orleans;
using Orleans.CodeGeneration;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orleans.CodeGeneration")]
[assembly: InternalsVisibleTo("Orleans.CodeGeneration.Build")]
[assembly: InternalsVisibleTo("Orleans.Clustering.AzureStorage")]
[assembly: InternalsVisibleTo("Orleans.Clustering.DynamoDB")]
[assembly: InternalsVisibleTo("Orleans.Hosting.AzureCloudServices")]
[assembly: InternalsVisibleTo("Orleans.Persistence.AzureStorage")]
[assembly: InternalsVisibleTo("Orleans.Persistence.DynamoDB")]
[assembly: InternalsVisibleTo("Orleans.Reminders.AzureStorage")]
[assembly: InternalsVisibleTo("Orleans.Reminders.DynamoDB")]
[assembly: InternalsVisibleTo("Orleans.Runtime")]
[assembly: InternalsVisibleTo("Orleans.Runtime.Abstractions")]
[assembly: InternalsVisibleTo("Orleans.Streaming.AzureStorage")]
[assembly: InternalsVisibleTo("Orleans.Streaming.EventHubs")]
[assembly: InternalsVisibleTo("Orleans.Streaming.SQS")]
[assembly: InternalsVisibleTo("Orleans.TelemetryConsumers.Counters")]
[assembly: InternalsVisibleTo("Orleans.TestingHost")]
[assembly: InternalsVisibleTo("Orleans.TestingHost.AppDomain")]
[assembly: InternalsVisibleTo("Orleans.TestingHost.Legacy")]
[assembly: InternalsVisibleTo("OrleansCounterControl")]
[assembly: InternalsVisibleTo("OrleansManager")]
[assembly: InternalsVisibleTo("OrleansProviders")]

// For PlacementResult and related types.
[assembly: InternalsVisibleTo("Orleans.Clustering.ServiceFabric")]

[assembly: InternalsVisibleTo("AWSUtils.Tests")]
[assembly: InternalsVisibleTo("DefaultCluster.Tests")]
[assembly: InternalsVisibleTo("GoogleUtils.Tests")]
[assembly: InternalsVisibleTo("LoadTestGrains")]
[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: InternalsVisibleTo("OrleansBenchmarks")]
[assembly: InternalsVisibleTo("Tester")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.AdoNet")]
[assembly: InternalsVisibleTo("Tester.ZooKeeperUtils")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("TestExtensions")]
[assembly: InternalsVisibleTo("TestInternalGrainInterfaces")]
[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("CodeGenerator.Tests")]

// Legacy provider support
[assembly: InternalsVisibleTo("Orleans.Core.Legacy")]
[assembly: InternalsVisibleTo("Orleans.Runtime.Legacy")]

[assembly: KnownAssembly(typeof(IGrain))]
