using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Orleans.TestingHost;
using TestExtensions;
using TestExtensions.Runners;
using UnitTests.StorageTests.ModelBased;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests;

/// <summary>
/// Tests for the in-memory grain storage provider.
///
/// Memory storage provider characteristics:
/// - Stores grain state in memory only (non-durable)
/// - State is lost when silo restarts
/// - Useful for development, testing, and caching scenarios
/// - Supports all standard persistence operations (Read, Write, Clear)
/// - Multiple named instances can be configured
///
/// These tests verify that memory storage correctly implements the
/// IGrainStorage interface and handles all persistence operations,
/// even though the storage is not durable across restarts.
///
/// The tests inherit from GrainPersistenceTestsRunner which provides
/// a comprehensive suite of persistence behavior tests.
/// </summary>
[TestCategory("Persistence"), TestCategory("Memory")]
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Runtime")]
public class MemoryGrainStorageTests : GrainPersistenceTestsRunner, IClassFixture<MemoryGrainStorageTests.Fixture>
{
    private const string MemoryStoreName = "MemoryStore";
    private const string MemoryStoreWithLatencyName = "MemoryStoreWithLatency";
    private readonly ITestOutputHelper output;

    public class Fixture : BaseTestClusterFixture
    {
        private class StorageSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                // Configure multiple named memory storage providers
                // Grains can specify which provider to use via [StorageProvider] attribute
                hostBuilder.AddMemoryGrainStorage("GrainStorageForTest")
                    .AddMemoryGrainStorage("test1")
                    .AddMemoryGrainStorage(MemoryStoreName);
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<StorageSiloBuilderConfigurator>();
        }
    }

    public MemoryGrainStorageTests(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
    {
        this.output = output;
        fixture.EnsurePreconditionsMet();
        // Memory storage is not durable - state is lost on restart
        // This flag tells the base test runner to skip durability tests
        IsDurableStorage = false;
    }

    [Fact, TestCategory("BVT"), TestCategory("Persistence"), TestCategory("Memory"), TestCategory("ModelBased")]
    public async Task GrainStorage_ModelBasedGeneratedConformance()
    {
        var storage = HostedCluster.GetSiloServiceProvider().GetRequiredKeyedService<IGrainStorage>(MemoryStoreName);
        var runner = new GrainStorageModelBasedTestRunner(storage, MemoryStoreName, output.WriteLine);
        await runner.RunGeneratedConformanceTests();
    }

    [Fact, TestCategory("BVT"), TestCategory("Persistence"), TestCategory("Memory"), TestCategory("ModelBased")]
    public async Task GrainStorageWithLatency_ModelBasedGeneratedConformance()
    {
        var services = HostedCluster.GetSiloServiceProvider();
        var storage = new MemoryGrainStorageWithLatency(
            MemoryStoreWithLatencyName,
            new MemoryStorageWithLatencyOptions { Latency = TimeSpan.Zero },
            services.GetRequiredService<ILoggerFactory>(),
            services.GetRequiredService<IGrainFactory>(),
            services.GetRequiredService<IActivatorProvider>(),
            services.GetRequiredService<IGrainStorageSerializer>());
        var runner = new GrainStorageModelBasedTestRunner(
            storage,
            new GrainStorageModelBasedConformanceOptions
            {
                ProviderName = MemoryStoreWithLatencyName
            },
            output.WriteLine);

        await runner.RunGeneratedConformanceTests();
    }
}
