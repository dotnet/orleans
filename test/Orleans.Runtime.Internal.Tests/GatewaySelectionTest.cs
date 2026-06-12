//#define USE_SQL_SERVER
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Internal;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.MessageCenterTests
{
    /// <summary>
    /// Tests for Orleans gateway selection and load balancing mechanisms.
    /// 
    /// Gateways are the entry points for client connections to an Orleans cluster.
    /// Each silo can act as a gateway, and clients need to:
    /// - Discover available gateways (via IGatewayListProvider)
    /// - Select which gateway to connect to
    /// - Handle gateway failures by switching to another gateway
    /// 
    /// The GatewayManager implements a round-robin selection strategy to distribute
    /// client connections evenly across available gateways. This is critical for:
    /// - Load balancing client connections
    /// - Avoiding gateway overload
    /// - Providing high availability for client access
    /// 
    /// These tests verify that gateway selection distributes load evenly.
    /// </summary>
    public class GatewaySelectionTest
    {
        protected readonly ITestOutputHelper output;

        protected static readonly List<Uri> gatewayAddressUris = new[]
        {
            new Uri("gwy.tcp://127.0.0.1:1/0"),
            new Uri("gwy.tcp://127.0.0.1:2/0"),
            new Uri("gwy.tcp://127.0.0.1:3/0"),
            new Uri("gwy.tcp://127.0.0.1:4/0")
        }.ToList();
        
        public GatewaySelectionTest(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Tests that the gateway manager properly distributes connections
        /// across available gateways using round-robin selection.
        /// Verifies even distribution across 4 test gateways.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Gateway")]
        public async Task GatewaySelection()
        {
            var listProvider = new TestListProvider(gatewayAddressUris);
            await Test_GatewaySelection(listProvider);
        }

        [Fact, TestCategory("BVT"), TestCategory("Gateway")]
        public async Task GatewayListUpdatedDiagnosticEventEmittedOnStart()
        {
            var gatewayUris = new List<Uri> { new("gwy.tcp://127.0.0.1:12345/42") };
            var listProvider = new TestListProvider(gatewayUris);
            using var observer = new Observer(GatewayEvents.AllEvents);
            using var gatewayManager = new GatewayManager(Options.Create(new GatewayOptions()), listProvider, NullLoggerFactory.Instance, null, TimeProvider.System);

            await gatewayManager.StartAsync(CancellationToken.None);

            var evt = Assert.Single(observer.Events.OfType<GatewayEvents.GatewayListUpdated>());
            Assert.Same(gatewayManager, evt.Source);
            var expectedGateway = SiloAddress.New(IPAddress.Loopback, 12345, 0);
            Assert.Equal(expectedGateway, Assert.Single(evt.KnownGateways));
            Assert.Equal(expectedGateway, Assert.Single(evt.LiveGateways));
        }

        /// <summary>
        /// Core test logic for gateway selection distribution.
        /// Simulates 2300 gateway selections and verifies they are evenly distributed.
        /// </summary>
        protected async Task Test_GatewaySelection(IGatewayListProvider listProvider)
        {
            IList<Uri> gatewayUris = await listProvider.GetGateways();
            Assert.True(gatewayUris.Count > 0, $"Found some gateways. Data = {Utils.EnumerableToString(gatewayUris)}");

            var gatewayEndpoints = gatewayUris.Select(uri =>
            {
                return new IPEndPoint(IPAddress.Parse(uri.Host), uri.Port);
            }).ToList();

            // Create and start the gateway manager with our test gateway list
            var gatewayManager = new GatewayManager(Options.Create(new GatewayOptions()), listProvider, NullLoggerFactory.Instance, null, TimeProvider.System);
            await gatewayManager.StartAsync(CancellationToken.None);

            var counts = new int[4];  // Track selections per gateway

            // Simulate 2300 gateway selections to test distribution
            for (int i = 0; i < 2300; i++)
            {
                var ip = gatewayManager.GetLiveGateway();
                Assert.NotNull(ip);
                var addr = ip.Endpoint.Address;
                Assert.Equal(IPAddress.Loopback, addr);  // "Incorrect IP address returned for gateway"
                Assert.True((0 < ip.Endpoint.Port) && (ip.Endpoint.Port < 5), "Incorrect IP port returned for gateway");
                counts[ip.Endpoint.Port - 1]++;  // Count selections per gateway
            }

            // Historical note: The gateway manager used to select randomly based on load,
            // but now uses round-robin for more predictable distribution.
            // The commented assertions show the old expected distribution patterns.

            // With round-robin selection, we expect exactly even distribution
            // 2300 selections / 4 gateways = 575 per gateway
            int low = 2300 / 4;
            int up = 2300 / 4;
            Assert.True((low <= counts[0]) && (counts[0] <= up), "Gateway selection is incorrectly skewed. " + counts[0]);
            Assert.True((low <= counts[1]) && (counts[1] <= up), "Gateway selection is incorrectly skewed. " + counts[1]);
            Assert.True((low <= counts[2]) && (counts[2] <= up), "Gateway selection is incorrectly skewed. " + counts[2]);
            Assert.True((low <= counts[3]) && (counts[3] <= up), "Gateway selection is incorrectly skewed. " + counts[3]);
        }

        private sealed class Observer : IObserver<GatewayEvents.GatewayEvent>, IDisposable
        {
            private readonly IDisposable _subscription;
            private readonly List<GatewayEvents.GatewayEvent> _events = [];

            public Observer(IObservable<GatewayEvents.GatewayEvent> observable)
            {
                _subscription = observable.Subscribe(this);
            }

            public GatewayEvents.GatewayEvent[] Events => _events.ToArray();

            public void Dispose() => _subscription.Dispose();

            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(GatewayEvents.GatewayEvent value) => _events.Add(value);
        }

        /// <summary>
        /// Test implementation of IGatewayListProvider that returns a fixed list of gateways.
        /// In production, this interface is implemented by membership providers
        /// (Azure Table, SQL, ZooKeeper, etc.) to dynamically discover available gateways.
        /// </summary>
        private class TestListProvider : IGatewayListProvider
        {
            private readonly IList<Uri> list;

            public TestListProvider(List<Uri> gatewayUris)
            {
                list = gatewayUris;
            }

            public Task<IList<Uri>> GetGateways()
            {
                return Task.FromResult(list);
            }

            public TimeSpan MaxStaleness
            {
                get { return TimeSpan.FromMinutes(1); }
            }

            public bool IsUpdatable
            {
                get { return false; }
            }
            public Task InitializeGatewayListProvider()
            {
                return Task.CompletedTask;
            }
        }
    }
}
