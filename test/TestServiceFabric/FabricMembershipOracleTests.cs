using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Orleans.ServiceFabric;
using Microsoft.Orleans.ServiceFabric.Models;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Xunit;

namespace TestServiceFabric
{
    using Xunit.Abstractions;

    [TestCategory("ServiceFabric"), TestCategory("BVT")]
    public class FabricMembershipOracleTests
    {
        private readonly MockSiloDetails siloDetails;
        private readonly MockResolver resolver;

        private ITestOutputHelper Output { get; }
        private readonly FabricMembershipOracle oracle;
        public FabricMembershipOracleTests(ITestOutputHelper output)
        {
            this.Output = output;
            this.siloDetails = new MockSiloDetails
            {
                Name = Guid.NewGuid().ToString("N"),
                SiloAddress = SiloAddress.NewLocalAddress(SiloAddress.AllocateNewGeneration())
            };

            this.resolver = new MockResolver();
            var globalConfig = new ClusterConfiguration().Globals;
            globalConfig.MaxMultiClusterGateways = 2;
            globalConfig.ClusterId = "MegaGoodCluster";

            this.oracle = new FabricMembershipOracle(
                this.siloDetails,
                globalConfig,
                this.resolver,
                name => new TestOutputLogger(this.Output, name));
        }

        [Fact]
        public async Task BasicLifecycle()
        {
            Assert.False(this.oracle.CheckHealth(DateTime.MinValue));
            Assert.DoesNotContain(this.oracle, this.resolver.Handlers);
            
            this.AssertStatus(SiloStatus.Created);
            Assert.Equal(0, this.resolver.RefreshCalled);

            await this.oracle.Start();
            AssertStatus(SiloStatus.Joining);
            Assert.Contains(this.oracle, this.resolver.Handlers);
            Assert.Equal(1, this.resolver.RefreshCalled);
            Assert.True(this.oracle.CheckHealth(DateTime.MinValue));

            await this.oracle.BecomeActive();
            AssertStatus(SiloStatus.Active);
            Assert.Equal(1, this.resolver.RefreshCalled);

            await this.oracle.ShutDown();
            AssertStatus(SiloStatus.ShuttingDown);

            await this.oracle.Stop();
            AssertStatus(SiloStatus.Stopping);
            Assert.DoesNotContain(this.oracle, this.resolver.Handlers);
            
            await this.oracle.KillMyself();
            AssertStatus(SiloStatus.Dead);
        }

        [Fact]
        public void ReturnsSiloDetails()
        {
            Assert.Equal(this.siloDetails.SiloAddress, this.oracle.SiloAddress);
            Assert.Equal(this.siloDetails.Name, this.oracle.SiloName);
            string actualName;
            Assert.True(this.oracle.TryGetSiloName(this.siloDetails.SiloAddress, out actualName));
            Assert.Equal(this.siloDetails.Name, actualName);
        }

        [Fact]
        public async Task DoesNotFailBeforeFirstResolverUpdate()
        {
            var listener = new MockStatusListener();
            this.oracle.SubscribeToSiloStatusEvents(listener);
            await this.oracle.Start();
            await this.oracle.BecomeActive();
            var allSilos = this.oracle.GetApproximateSiloStatuses(false);
            var livingSilos = this.oracle.GetApproximateSiloStatuses(true);
            Assert.Equal(1, allSilos.Count);
            Assert.Equal(1, livingSilos.Count);
            Assert.Equal(2, listener.Notifications.Count);
            Assert.Equal(1, listener.Silos.Count);

            await this.oracle.Stop();
            allSilos = this.oracle.GetApproximateSiloStatuses(false);
            livingSilos = this.oracle.GetApproximateSiloStatuses(true);
            Assert.Equal(1, allSilos.Count);
            Assert.Equal(0, livingSilos.Count);
            Assert.Equal(3, listener.Notifications.Count);
            Assert.Equal(1, listener.Silos.Count);
        }

        /// <summary>
        /// Tests that new silos are propagated through to listeners.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact]
        public async Task HandlesSiloAdditionAndRemoval()
        {
            var listener = new MockStatusListener();
            this.oracle.SubscribeToSiloStatusEvents(listener);
            await this.oracle.Start();
            await this.oracle.BecomeActive();
            var multiClusters = this.oracle.GetApproximateMultiClusterGateways();
            Assert.Equal(1, multiClusters.Count);
            Assert.Contains(this.siloDetails.SiloAddress, multiClusters);

            var silos = new[]
            {
                CreateSiloInfo(
                    SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1), 1),
                    SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 2), 1),
                    "HappyNewSilo"),
                CreateSiloInfo(
                    SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 3), 2),
                    SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 4), 2),
                    "OtherNewSilo"),
            };

            this.resolver.Notify(silos);
            Assert.Equal(3, listener.Silos.Count);
            Assert.Contains(silos[1].SiloAddress, listener.Silos.Keys);
            Assert.Equal(SiloStatus.Active, listener.Silos[silos[1].SiloAddress]);
            AssertStatus(silos[0].SiloAddress, SiloStatus.Active);
            AssertStatus(silos[1].SiloAddress, SiloStatus.Active);
            multiClusters = this.oracle.GetApproximateMultiClusterGateways();
            Assert.Equal(2, multiClusters.Count);

            // Send the same update again and verify that nothing changed.
            this.resolver.Notify(silos);
            Assert.Equal(3, listener.Silos.Count);
            Assert.Contains(silos[1].SiloAddress, listener.Silos.Keys);
            Assert.Equal(SiloStatus.Active, listener.Silos[silos[1].SiloAddress]);
            multiClusters = this.oracle.GetApproximateMultiClusterGateways();
            Assert.Equal(2, multiClusters.Count);

            // Remove a silo and verify that it's been removed.
            this.resolver.Notify(new[] {silos[1]});
            Assert.Equal(3, listener.Silos.Count);
            Assert.Contains(silos[1].SiloAddress, listener.Silos.Keys);
            Assert.Equal(SiloStatus.Active, listener.Silos[silos[1].SiloAddress]);
            AssertStatus(silos[1].SiloAddress, SiloStatus.Active);
            Assert.Equal(SiloStatus.Dead, listener.Silos[silos[0].SiloAddress]);
            AssertStatus(silos[0].SiloAddress, SiloStatus.None);
            multiClusters = this.oracle.GetApproximateMultiClusterGateways();
            Assert.Equal(2, multiClusters.Count);

            // Remove a silo and verify that it's been removed.
            this.resolver.Notify(new FabricSiloInfo[0]);
            Assert.Equal(3, listener.Silos.Count);
            Assert.Contains(silos[1].SiloAddress, listener.Silos.Keys);
            Assert.Equal(SiloStatus.Dead, listener.Silos[silos[0].SiloAddress]);
            AssertStatus(silos[0].SiloAddress, SiloStatus.None);
            Assert.Equal(SiloStatus.Dead, listener.Silos[silos[1].SiloAddress]);
            AssertStatus(silos[1].SiloAddress, SiloStatus.None);

            multiClusters = this.oracle.GetApproximateMultiClusterGateways();
            Assert.Equal(1, multiClusters.Count);
            Assert.Contains(this.siloDetails.SiloAddress, multiClusters);
        }

        private class MockSiloDetails : ILocalSiloDetails
        {
            public string Name { get; set; }
            public SiloAddress SiloAddress { get; set; }
        }

        private void AssertStatus(SiloStatus expected)
        {
            AssertStatus(this.siloDetails.SiloAddress, expected);
        }

        private void AssertStatus(SiloAddress address, SiloStatus expected)
        {
            var localStatus = this.oracle.GetApproximateSiloStatus(address);
            Assert.Equal(expected, localStatus);
            if (address.Equals(this.siloDetails.SiloAddress)) Assert.Equal(localStatus, this.oracle.CurrentStatus);
            Assert.Equal(!address.Equals(this.siloDetails.SiloAddress) && expected == SiloStatus.Dead, this.oracle.IsDeadSilo(address));
            Assert.Equal(address.Equals(this.siloDetails.SiloAddress) || !expected.IsTerminating(), this.oracle.IsFunctionalDirectory(address));
        }

        private static FabricSiloInfo CreateSiloInfo(SiloAddress silo, SiloAddress gateway, string name)
        {
            return new FabricSiloInfo
            {
                Name = name,
                Silo = silo.ToParsableString(),
                Gateway = gateway.ToParsableString()
            };
        }

        private class MockStatusListener : ISiloStatusListener
        {
            public Dictionary<SiloAddress, SiloStatus> Silos { get; } = new Dictionary<SiloAddress, SiloStatus>();
            public List<Tuple<SiloAddress, SiloStatus>> Notifications { get; } = new List<Tuple<SiloAddress, SiloStatus>>();

            public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
            {
                this.Notifications.Add(Tuple.Create(updatedSilo, status));
                this.Silos[updatedSilo] = status;
            }
        }

        private class MockResolver : IFabricServiceSiloResolver
        {
            public HashSet<IFabricServiceStatusListener> Handlers { get; } = new HashSet<IFabricServiceStatusListener>();

            public int RefreshCalled { get; private set; }

            public void Subscribe(IFabricServiceStatusListener handler)
            {
                this.Handlers.Add(handler);
            }

            public void Unsubscribe(IFabricServiceStatusListener handler)
            {
                this.Handlers.Remove(handler);
            }

            public Task Refresh()
            {
                this.RefreshCalled++;
                return Task.FromResult(0);
            }

            public void Notify(FabricSiloInfo[] update)
            {
                foreach (var handler in this.Handlers) handler.OnUpdate(update);
            }

            public void Reset()
            {
                this.Handlers.Clear();
                this.RefreshCalled = 0;
            }
        }
    }
}
