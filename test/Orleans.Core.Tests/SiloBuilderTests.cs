using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Configuration.Validators;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Statistics;
using UnitTests.Grains;
using Xunit;

namespace NonSilo.Tests
{
    /// <summary>
    /// A no-op implementation of IMembershipTable used for testing silo configuration
    /// without requiring actual membership table infrastructure.
    /// </summary>
    public class NoOpMembershipTable : IMembershipTable
    {
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            return Task.CompletedTask;
        }

        public Task DeleteMembershipTableEntries(string clusterId)
        {
            return Task.CompletedTask;
        }

        public Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            return Task.CompletedTask;
        }

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            return Task.FromResult(true);
        }

        public Task<MembershipTableData> ReadAll()
        {
            throw new NotImplementedException();
        }

        public Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            throw new NotImplementedException();
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            return Task.CompletedTask;
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Tests for the Orleans SiloBuilder, which is responsible for configuring and building Orleans silo instances.
    /// These tests verify configuration validation, service registration, and proper initialization of silo components
    /// without requiring a full Orleans cluster. Silos are the primary hosting units for grains in Orleans.
    /// </summary>
    [TestCategory("BVT")]
    [TestCategory("Hosting")]
    public class SiloBuilderTests
    {
        /// <summary>
        /// Tests basic silo builder configuration, verifying that a silo can be successfully built
        /// with localhost clustering and required configuration options.
        /// </summary>
        [Fact]
        public void SiloBuilderTest()
        {
            var host = new HostBuilder()
                .UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
                        .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback);
                })
                .UseDefaultServiceProvider((context, options) =>
                {
                    options.ValidateScopes = true;
                    options.ValidateOnBuild = true;
                })
                .Build();

            var clusterClient = host.Services.GetRequiredService<IClusterClient>();
        }

        /// <summary>
        /// Grain's CollectionAgeLimit must be > 0 minutes.
        /// </summary>
        [Fact]
        public async Task SiloBuilder_GrainCollectionOptionsForZeroSecondsAgeLimitTest()
        {
            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .Configure<ClusterOptions>(options => { options.ClusterId = "GrainCollectionClusterId"; options.ServiceId = "GrainCollectionServiceId"; })
                        .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                        .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                        .Configure<GrainCollectionOptions>(options => options
                                    .ClassSpecificCollectionAge
                                    .Add(typeof(CollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain).FullName, TimeSpan.Zero));
                }).RunConsoleAsync();
            });
        }

        /// <summary>
        /// ClusterMembershipOptions.NumProbedSilos must be greater than ClusterMembershipOptions.NumVotesForDeathDeclaration.
        /// </summary>
        [Fact]
        public async Task SiloBuilder_ClusterMembershipOptionsValidators()
        {
            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterMembershipOptions>(options => { options.NumVotesForDeathDeclaration = 10; options.NumProbedSilos = 1; });
                }).RunConsoleAsync();
            });

            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterMembershipOptions>(options => { options.NumVotesForDeathDeclaration = 0; });
                }).RunConsoleAsync();
            });
        }

        /// <summary>
        /// Ensures <see cref="LoadSheddingValidator"/> fails when LoadSheddingLimit greater than 100.
        /// </summary>
        [Fact]
        public async Task SiloBuilder_LoadSheddingValidatorAbove100ShouldFail()
        {
            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
                        .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                        .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                        .Configure<LoadSheddingOptions>(options =>
                        {
                            options.LoadSheddingEnabled = true;
                            options.CpuThreshold = 101;
                        });
                }).RunConsoleAsync();
            });
        }

        /// <summary>
        /// Tests that a silo cannot start without any grain classes or interfaces registered.
        /// This ensures silos have actual work to perform before they can be started.
        /// </summary>
        [Fact]
        public async Task SiloBuilderThrowsDuringStartupIfNoGrainsAdded()
        {
            using var host = new HostBuilder()
                .UseOrleans((ctx, siloBuilder) =>
                {
                    // Add only an assembly with generated serializers but no grain interfaces or grain classes
                    siloBuilder.UseLocalhostClustering()
                    .Configure<GrainTypeOptions>(options =>
                    {
                        options.Classes.Clear();
                        options.Interfaces.Clear();
                    });
                }).Build();

            await Assert.ThrowsAsync<OrleansConfigurationException>(() => host.StartAsync());
        }

        /// <summary>
        /// Tests that attempting to configure both a client and a silo in the same host throws an exception.
        /// Orleans requires separate hosts for silos and clients.
        /// </summary>
        [Fact]
        public void SiloBuilderThrowsDuringStartupIfClientBuildersAdded()
        {
            Assert.Throws<OrleansConfigurationException>(() =>
            {
                _ = new HostBuilder()
                    .UseOrleansClient((ctx, clientBuilder) =>
                    {
                        clientBuilder.UseLocalhostClustering();
                    })
                    .UseOrleans((ctx, siloBuilder) =>
                    {
                        siloBuilder.UseLocalhostClustering();
                    });
            });
        }

        /// <summary>
        /// Tests that attempting to configure both a client and a silo using the Host.CreateApplicationBuilder API throws an exception.
        /// This verifies that the same restriction applies to the modern hosting API.
        /// </summary>
        [Fact]
        public void SiloBuilderWithHotApplicationBuilderThrowsDuringStartupIfClientBuildersAdded()
        {
            Assert.Throws<OrleansConfigurationException>(() =>
            {
                _ = Host.CreateApplicationBuilder()
                    .UseOrleansClient(clientBuilder =>
                    {
                        clientBuilder.UseLocalhostClustering();
                    })
                    .UseOrleans(siloBuilder =>
                    {
                        siloBuilder.UseLocalhostClustering();
                    });
            });
        }

        private class MyService
        {
            public int Id { get; set; }
        }
    }
}
