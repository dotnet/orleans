using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Configuration.Validators;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Statistics;
using Orleans.TestingHost;
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
        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

        public Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

        public Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

        public Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

        public Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        [Obsolete("Use ReadAllAsync instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

        public Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotImplementedException();
        }

        [Obsolete("Use ReadRowAsync instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

        public Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotImplementedException();
        }

        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

        public Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

        public Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
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
                                    .Add(typeof(CollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain).FullName!, TimeSpan.Zero));
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
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
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
            });

            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterMembershipOptions>(options => { options.NumVotesForDeathDeclaration = 0; });
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
            });

            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterMembershipOptions>(options => options.MaxDefunctSiloEntries = -1);
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
            });

            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterMembershipOptions>(options => options.ProbeTimeout = TimeSpan.Zero);
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
            });

            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterMembershipOptions>(options => options.MinProbeTimeout = TimeSpan.FromSeconds(6));
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
            });

            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterMembershipOptions>(options =>
                        {
                            options.MinProbeTimeout = TimeSpan.FromSeconds(1);
                            options.MaxProbeTimeout = TimeSpan.FromSeconds(4);
                        });
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
            });

            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterMembershipOptions>(options => options.MaxProbeTimeout = TimeSpan.FromDays(50));
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
            });

            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterMembershipOptions>(options =>
                        {
                            options.ProbeTimeout = TimeSpan.FromDays(1);
                            options.MaxProbeTimeout = TimeSpan.FromDays(49);
                            options.NumMissedProbesLimit = int.MaxValue;
                        });
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
            });

            await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
            {
                await new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterMembershipOptions>(options =>
                        {
                            options.ProbeTimeout = TimeSpan.FromTicks(4_611_686_018);
                            options.MaxProbeTimeout = options.ProbeTimeout;
                            options.NumMissedProbesLimit = 2_000_000_000;
                        });
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
            });
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(-1L)]
        [InlineData(-TimeSpan.TicksPerMillisecond - 1)]
        [InlineData(long.MinValue)]
        public async Task SiloBuilder_ClusterMembershipOptionsRejectsInvalidTableRefreshTimeout(long ticks)
        {
            using var host = new HostBuilder()
                .UseOrleans(siloBuilder => siloBuilder
                    .UseLocalhostClustering()
                    .Configure<ClusterMembershipOptions>(options => options.TableRefreshTimeout = TimeSpan.FromTicks(ticks)))
                .Build();

            var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(
                () => host.StartAsync(TestContext.Current.CancellationToken));

            Assert.Contains("ClusterMembershipOptions.TableRefreshTimeout", exception.Message);
            Assert.Contains("must be greater than 0 or Timeout.InfiniteTimeSpan", exception.Message);
        }

        [Theory]
        [InlineData(1L)]
        [InlineData(TimeSpan.TicksPerMillisecond / 2)]
        [InlineData(60L * TimeSpan.TicksPerDay)]
        [InlineData(-TimeSpan.TicksPerMillisecond)]
        public void SiloBuilder_ClusterMembershipOptionsAcceptsValidTableRefreshTimeout(long ticks)
        {
            using var services = new ServiceCollection()
                .AddSingleton<IMembershipTable, NoOpMembershipTable>()
                .Configure<ClusterMembershipOptions>(options => options.TableRefreshTimeout = TimeSpan.FromTicks(ticks))
                .BuildServiceProvider();

            new SiloClusteringValidator(services).ValidateConfiguration();
        }

        [Fact]
        public async Task SiloBuilder_InfiniteTableRefreshTimeoutStartsAndStopsWithoutFatalErrors()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var refreshWait = new TaskCompletionSource<(TimeSpan? Delay, Task<bool> Tick)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            fatalErrorHandler.IsUnexpected(Arg.Any<Exception>()).Returns(true);
            using var portAllocator = new TestClusterPortAllocator();
            var (siloPort, gatewayPort) = portAllocator.AllocateConsecutivePortPairs(1);
            using var host = new HostBuilder()
                .UseOrleans(siloBuilder => siloBuilder
                    .UseLocalhostClustering(siloPort, gatewayPort)
                    .Configure<ClusterMembershipOptions>(options => options.TableRefreshTimeout = Timeout.InfiniteTimeSpan)
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(fatalErrorHandler);
                        services.AddSingleton<IAsyncTimerFactory>(serviceProvider =>
                        {
                            var timerFactory = new AsyncTimerFactory(serviceProvider.GetRequiredService<ILoggerFactory>());
                            var observedFactory = Substitute.For<IAsyncTimerFactory>();
                            observedFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<TimeProvider>())
                                .Returns(call =>
                                {
                                    var name = call.ArgAt<string>(1);
                                    var timer = timerFactory.Create(call.ArgAt<TimeSpan>(0), name, call.ArgAt<TimeProvider>(2));
                                    return name == "PeriodicallyRefreshMembershipTable"
                                        ? new ObservedMembershipRefreshTimer(timer, refreshWait)
                                        : timer;
                                });
                            return observedFactory;
                        });
                    }))
                .Build();

            try
            {
                await host.StartAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                var (delay, tick) = await refreshWait.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

                Assert.Equal(Timeout.InfiniteTimeSpan, delay);
                Assert.False(tick.IsCompleted);
                Assert.Equal(SiloStatus.Active, host.Services.GetRequiredService<IMembershipManager>().LocalSiloStatus);
            }
            finally
            {
                await host.StopAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            var stoppedRefresh = await refreshWait.Task;
            Assert.False(await stoppedRefresh.Tick.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
            fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default!, default!, default!);
        }

        private sealed class ObservedMembershipRefreshTimer(
            IAsyncTimer timer,
            TaskCompletionSource<(TimeSpan? Delay, Task<bool> Tick)> refreshWait) : IAsyncTimer
        {
            public Task<bool> NextTick(TimeSpan? overrideDelay = default)
            {
                var tick = timer.NextTick(overrideDelay);
                refreshWait.TrySetResult((overrideDelay, tick));
                return tick;
            }

            public bool CheckHealth(DateTime lastCheckTime, [NotNullWhen(false)] out string? reason) => timer.CheckHealth(lastCheckTime, out reason);

            public void Dispose() => timer.Dispose();
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
                }).RunConsoleAsync(TestContext.Current.CancellationToken);
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

            await Assert.ThrowsAsync<OrleansConfigurationException>(
                () => host.StartAsync(TestContext.Current.CancellationToken));
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
