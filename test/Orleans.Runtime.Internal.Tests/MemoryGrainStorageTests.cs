using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.TestingHost;
using TestExtensions;
using TestExtensions.Runners;
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
public class MemoryGrainStorageTests : GrainPersistenceTestsRunner, IClassFixture<MemoryGrainStorageTests.Fixture>
{
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
                .AddMemoryGrainStorage("MemoryStore");
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<StorageSiloBuilderConfigurator>();
        }
    }

    public MemoryGrainStorageTests(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
    {
        fixture.EnsurePreconditionsMet();
        // Memory storage is not durable - state is lost on restart
        // This flag tells the base test runner to skip durability tests
        IsDurableStorage = false;
    }
}
