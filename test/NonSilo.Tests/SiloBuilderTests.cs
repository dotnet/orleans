using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Configuration.Validators;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Statistics;
using UnitTests.Grains;
using Xunit;

namespace NonSilo.Tests
{
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
    /// Tests for <see cref="ISiloBuilder"/>.
    /// </summary>
    [TestCategory("BVT")]
    [TestCategory("Hosting")]
    public class SiloBuilderTests
    {
        [Fact]
        public void SiloBuilderTest()
        {
            var host = new HostBuilder()
                .UseOrleans(siloBuilder =>
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
                await new HostBuilder().UseOrleans(siloBuilder =>
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
        /// Ensures <see cref="LoadSheddingValidator"/> fails when LoadSheddingLimit greater than 100.
        /// </summary>
        [Fact]
        public async Task SiloBuilder_LoadSheddingValidatorAbove100ShouldFail()
        {
            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans(siloBuilder =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
                        .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                        .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                        .Configure<LoadSheddingOptions>(options =>
                        {
                            options.LoadSheddingEnabled = true;
                            options.LoadSheddingLimit = 101;
                        })
                        .ConfigureServices(svcCollection =>
                        {
                            svcCollection.AddSingleton<FakeHostEnvironmentStatistics>();
                            svcCollection.AddFromExisting<IHostEnvironmentStatistics, FakeHostEnvironmentStatistics>();
                            svcCollection.AddTransient<IConfigurationValidator, LoadSheddingValidator>();
                        });
                }).RunConsoleAsync();
            });
        }

        /// <summary>
        /// Ensures <see cref="LoadSheddingValidator"/> fails validation when invalid/no instance of
        /// <see cref="IHostEnvironmentStatistics"/> is registered using otherwise valid <see cref="LoadSheddingOptions"/>.
        /// </summary>
        [Fact]
        public async Task SiloBuilder_LoadSheddingValidatorFailsWithNoRegisteredHostEnvironmentStatistics()
        {
            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans(siloBuilder =>
                {
                    siloBuilder
                      .UseLocalhostClustering()
                      .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
                      .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                      .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                      .Configure<LoadSheddingOptions>(options =>
                      {
                          options.LoadSheddingEnabled = true;
                          options.LoadSheddingLimit = 95;
                      }).ConfigureServices(svcCollection =>
                      {
                          svcCollection.AddTransient<IConfigurationValidator, LoadSheddingValidator>();
                      });
                }).RunConsoleAsync();
            });
        }

        [Fact]
        public async Task SiloBuilderThrowsDuringStartupIfNoGrainsAdded()
        {
            using var host = new HostBuilder()
                .UseOrleans(siloBuilder =>
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

        [Fact]
        public void SiloBuilderThrowsDuringStartupIfClientBuildersAdded()
        {
            Assert.Throws<OrleansConfigurationException>(() =>
            {
                _ = new HostBuilder()
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

        private class FakeHostEnvironmentStatistics : IHostEnvironmentStatistics
        {
            public long? TotalPhysicalMemory => 0;

            public float? CpuUsage => 0;

            public long? AvailableMemory => 0;
        }

        private class MyService
        {
            public int Id { get; set; }
        }
    }
}