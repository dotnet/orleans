using Orleans;
using Orleans.CodeGeneration;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orleans.CodeGeneration")]
[assembly: InternalsVisibleTo("Orleans.CodeGeneration.Build")]
[assembly: InternalsVisibleTo("Orleans.Runtime")]
[assembly: InternalsVisibleTo("Orleans.Runtime.Abstractions")]
[assembly: InternalsVisibleTo("Orleans.Streaming")]
[assembly: InternalsVisibleTo("Orleans.TelemetryConsumers.Counters")]
[assembly: InternalsVisibleTo("Orleans.TelemetryConsumers.Linux")]
[assembly: InternalsVisibleTo("Orleans.TestingHost")]

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
[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("CodeGenerator.Tests")]

// Mocking libraries
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
