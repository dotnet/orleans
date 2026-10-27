using System.Collections.ObjectModel;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester.ClientConnectionTests
{
    [TestSuite("Functional")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("Functional")]
    public class ClusterClientTests : TestClusterPerTest
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
        }

        /// <summary>
        /// Ensures that ClusterClient.Connect can be retried.
        /// </summary>
        [Fact]
        public async Task ConnectIsRetryableTest()
        {
            var gateways = await this.HostedCluster.Client!.ServiceProvider.GetRequiredService<IGatewayListProvider>().GetGateways(); // The fixture deploys the client.
            var gwEndpoint = gateways.First();

            // Create a client with no gateway endpoint and then add a gateway endpoint when the client fails to connect.
            var gatewayProvider = new MockGatewayListProvider();
            var exceptions = new List<Exception>();

            Task<bool> RetryFunc(Exception exception, CancellationToken cancellationToken)
            {
                Assert.IsType<SiloUnavailableException>(exception);
                exceptions.Add(exception);
                gatewayProvider.Gateways = new List<Uri> { gwEndpoint }.AsReadOnly();
                return Task.FromResult(true);
            }

            using var host = new HostBuilder().UseOrleansClient((ctx, clientBuilder) =>
                {
                    clientBuilder
                        .Configure<ClusterOptions>(options =>
                        {
                            var existingClientOptions = this.HostedCluster.ServiceProvider
                                .GetRequiredService<IOptions<ClusterOptions>>().Value;
                            options.ClusterId = existingClientOptions.ClusterId;
                            options.ServiceId = existingClientOptions.ServiceId;
                        })
                        .ConfigureServices(services => services.AddSingleton<IGatewayListProvider>(gatewayProvider))
                        .UseConnectionRetryFilter(RetryFunc);
                })
                .Build();

            var client = host.Services.GetRequiredService<IClusterClient>();

            await host.StartAsync(TestContext.Current.CancellationToken);
            Assert.Single(exceptions);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task StartAsync_CancellationWhileWaitingForManifest_CancelsStartup(bool stopApplication, bool pendingRequest)
        {
            var gateways = await HostedCluster.Client!.ServiceProvider.GetRequiredService<IGatewayListProvider>().GetGateways();
            var clusterOptions = HostedCluster.ServiceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value;
            var manifestRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var manifestRequestCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            using var host = new HostBuilder()
                .UseOrleansClient(clientBuilder => clientBuilder
                    .Configure<ClusterOptions>(options =>
                    {
                        options.ClusterId = clusterOptions.ClusterId;
                        options.ServiceId = clusterOptions.ServiceId;
                    })
                    .UseStaticClustering(options => options.Gateways = [.. gateways])
                    .AddOutgoingGrainCallFilter(async context =>
                    {
                        if (context.InterfaceName.Contains(nameof(IClusterManifestSystemTarget), StringComparison.Ordinal))
                        {
                            manifestRequested.TrySetResult();
                            try
                            {
                                if (pendingRequest)
                                {
                                    await Task.Delay(Timeout.InfiniteTimeSpan, context.Request.GetCancellationToken());
                                }

                                throw new InvalidOperationException("Manifest retrieval is unavailable during client startup.");
                            }
                            finally
                            {
                                manifestRequestCompleted.TrySetResult();
                            }
                        }

                        await context.Invoke();
                    }))
                .Build();

            var start = host.StartAsync(cancellation.Token);
            try
            {
                await manifestRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.False(start.IsCompleted);

                if (stopApplication)
                {
                    host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                }
                else
                {
                    cancellation.Cancel();
                }

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                Assert.True(start.IsCanceled);
                await manifestRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                AssertConnectionsClosed(host);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await host.StopAsync(shutdown.Token);
                if (stopApplication)
                {
                    await ((IAsyncDisposable)host).DisposeAsync();
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StartAsync_CancellationWhileWaitingForGateways_CancelsStartup(bool blockInitialization)
        {
            var provider = new BlockingGatewayListProvider(blockInitialization);
            using var host = new HostBuilder()
                .UseOrleansClient(builder => builder.UseLocalhostClustering()
                    .ConfigureServices(services => services.AddSingleton<IGatewayListProvider>(provider)))
                .Build();
            using var cancellation = new CancellationTokenSource();
            var start = host.StartAsync(cancellation.Token);
            try
            {
                await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                Assert.True(start.IsCanceled);
                AssertConnectionsClosed(host);
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await host.StopAsync(shutdown.Token);
            }
            finally
            {
                provider.Release.TrySetResult();
            }
        }

        [Fact]
        public async Task StartAsync_CancellationWhileWaitingForRetryFilter_CancelsStartup()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var host = new HostBuilder()
                .UseOrleansClient(builder => builder.UseLocalhostClustering()
                    .ConfigureServices(services => services.AddSingleton<IGatewayListProvider>(new MockGatewayListProvider()))
                    .UseConnectionRetryFilter((_, _) =>
                    {
                        entered.TrySetResult();
                        return release.Task;
                    }))
                .Build();
            using var cancellation = new CancellationTokenSource();
            var start = host.StartAsync(cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                Assert.True(start.IsCanceled);
                AssertConnectionsClosed(host);
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await host.StopAsync(shutdown.Token);
            }
            finally
            {
                release.TrySetResult(false);
            }
        }

        [Fact]
        public async Task StartAsync_CancellationWhileConnecting_CancelsStartupAndConnectionAttempt()
        {
            var connector = new BlockingConnector();
            using var host = new HostBuilder()
                .UseOrleansClient(builder => builder.UseLocalhostClustering()
                    .Configure<ConnectionOptions>(options => options.OpenConnectionTimeout = TimeSpan.FromMinutes(1))
                    .ConfigureServices(services => services.AddKeyedSingleton<IConnectionFactory>(ClientOutboundConnectionFactory.ServicesKey, connector)))
                .Build();
            using var cancellation = new CancellationTokenSource();
            var start = host.StartAsync(cancellation.Token);
            await connector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(start.IsCanceled);
            await connector.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            AssertConnectionsClosed(host);
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.StopAsync(shutdown.Token);
        }

        private static void AssertConnectionsClosed(IHost host)
        {
            var connections = host.Services.GetRequiredService<ConnectionManager>();
            Assert.True(connections.Closed.IsCompletedSuccessfully);
            Assert.Equal(0, connections.ConnectionCount);
        }

        private sealed class BlockingGatewayListProvider(bool blockInitialization) : IGatewayListProvider
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TimeSpan MaxStaleness => TimeSpan.FromHours(1);
            public bool IsUpdatable => true;

            public Task InitializeGatewayListProvider() => blockInitialization ? BlockAsync() : Task.CompletedTask;

            public async Task<IList<Uri>> GetGateways()
            {
                await BlockAsync();
                return [];
            }

            private Task BlockAsync()
            {
                Entered.TrySetResult();
                return Release.Task;
            }
        }

        private sealed class BlockingConnector : IConnectionFactory
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public async ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
            {
                Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("The connection attempt must be canceled.");
                }
                finally
                {
                    Completed.TrySetResult();
                }
            }
        }

        public class MockGatewayListProvider : IGatewayListProvider
        {
            public ReadOnlyCollection<Uri> Gateways { get; set; } = new ReadOnlyCollection<Uri>(new List<Uri>());

            public Task InitializeGatewayListProvider() => Task.CompletedTask;

            public Task<IList<Uri>> GetGateways() => Task.FromResult<IList<Uri>>(this.Gateways);

            public TimeSpan MaxStaleness => TimeSpan.FromSeconds(30);

            public bool IsUpdatable => true;
        }
    }
}
