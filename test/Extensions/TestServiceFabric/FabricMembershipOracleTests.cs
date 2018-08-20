/*
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Clustering.ServiceFabric;
using Orleans.Clustering.ServiceFabric.Models;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.ServiceFabric;
using TestExtensions;
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
        private readonly UnknownSiloMonitor unknownSiloMonitor;
        private readonly ServiceFabricClusteringOptions fabricClusteringOptions;

        public FabricMembershipOracleTests(ITestOutputHelper output)
        {
            this.Output = output;
            this.siloDetails = new MockSiloDetails
            {
                Name = Guid.NewGuid().ToString("N"),
                SiloAddress = SiloAddressUtils.NewLocalSiloAddress(SiloAddress.AllocateNewGeneration())
            };

            this.resolver = new MockResolver();
            var multiClusterOptions = new MultiClusterOptions
            {
                HasMultiClusterNetwork = true,
                MaxMultiClusterGateways = 2
            };

            this.fabricClusteringOptions = new ServiceFabricClusteringOptions();
            this.unknownSiloMonitor = new UnknownSiloMonitor(
                new OptionsWrapper<ServiceFabricClusteringOptions>(this.fabricClusteringOptions),
                new TestOutputLogger<UnknownSiloMonitor>(this.Output));
            this.oracle = new FabricMembershipOracle(
                this.siloDetails,
                this.resolver,
                new NullLogger<FabricMembershipOracle>(),
                this.unknownSiloMonitor,
                Options.Create(multiClusterOptions));
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
            
            await this.oracle.KillMyself();
            Assert.DoesNotContain(this.oracle, this.resolver.Handlers);
            AssertStatus(SiloStatus.Dead);
        }

        [Fact]
        public void ReturnsSiloDetails()
        {
            Assert.Equal(this.siloDetails.SiloAddress, this.oracle.SiloAddress);
            Assert.Equal(this.siloDetails.Name, this.oracle.SiloName);
            Assert.True(this.oracle.TryGetSiloName(this.siloDetails.SiloAddress, out var actualName));
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
            Assert.Single(allSilos);
            Assert.Single(livingSilos);
            Assert.Equal(2, listener.Notifications.Count);
            Assert.Single(listener.Silos);

            await this.oracle.Stop();
            allSilos = this.oracle.GetApproximateSiloStatuses(false);
            livingSilos = this.oracle.GetApproximateSiloStatuses(true);
            Assert.Single(allSilos);
            Assert.Empty(livingSilos);
            Assert.Equal(3, listener.Notifications.Count);
            Assert.Single(listener.Silos);
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
            
            // The local silo transitioned to 'Joining' and then 'Active' and two other silos became 'Active',
            // so the version must be 4
            this.resolver.Notify(silos);
            listener.WaitForVersion(4);
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
            // The single removal will bump the version to 5.
            this.resolver.Notify(new[] {silos[1]});
            listener.WaitForVersion(5);
            Assert.Equal(3, listener.Silos.Count);
            Assert.Contains(silos[1].SiloAddress, listener.Silos.Keys);
            Assert.Equal(SiloStatus.Active, listener.Silos[silos[1].SiloAddress]);
            AssertStatus(silos[1].SiloAddress, SiloStatus.Active);
            Assert.Equal(SiloStatus.Dead, listener.Silos[silos[0].SiloAddress]);
            AssertStatus(silos[0].SiloAddress, SiloStatus.Dead);
            multiClusters = this.oracle.GetApproximateMultiClusterGateways();
            Assert.Equal(2, multiClusters.Count);

            // Remove a silo and verify that it's been removed.
            this.resolver.Notify(new FabricSiloInfo[0]);
            listener.WaitForVersion(6);

            Assert.Equal(3, listener.Silos.Count);
            Assert.Contains(silos[1].SiloAddress, listener.Silos.Keys);
            Assert.Equal(SiloStatus.Dead, listener.Silos[silos[0].SiloAddress]);
            AssertStatus(silos[0].SiloAddress, SiloStatus.Dead);
            Assert.Equal(SiloStatus.Dead, listener.Silos[silos[1].SiloAddress]);
            AssertStatus(silos[1].SiloAddress, SiloStatus.Dead);

            multiClusters = this.oracle.GetApproximateMultiClusterGateways();
            Assert.Equal(1, multiClusters.Count);
            Assert.Contains(this.siloDetails.SiloAddress, multiClusters);
        }

        /// <summary>
        /// Tests that unknown silos are accounted for when updates occur.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        /// <remarks>
        /// This is an integration test between <see cref="FabricMembershipOracle"/> and <see cref="UnknownSiloMonitorTests"/>.
        /// </remarks>
        [Fact]
        public async Task UnknownSilosAreAccountedForDuringUpdate()
        {
            var now = new[] {DateTime.UtcNow};
            this.unknownSiloMonitor.GetDateTime = () => now[0];

            var listener = new MockStatusListener();
            this.oracle.SubscribeToSiloStatusEvents(listener);
            await this.oracle.Start();
            await this.oracle.BecomeActive();

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
            listener.WaitForVersion(4);
            Assert.Equal(3, listener.Silos.Count);

            // Query for an unknown silo. Doing so should result in the silo being recorded as unknown.
            var unknownSilo1 = SiloAddress.New(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 3), 3);
            Assert.Equal(SiloStatus.None, this.oracle.GetApproximateSiloStatus(unknownSilo1));
            this.resolver.Notify(silos);

            // The status should not have changed.
            Assert.Equal(SiloStatus.None, this.oracle.GetApproximateSiloStatus(unknownSilo1));

            now[0] += this.fabricClusteringOptions.UnknownSiloRemovalPeriod + TimeSpan.FromMilliseconds(1);
            this.resolver.Notify(silos);
            listener.WaitForVersion(5);

            // After the delay, the silo should be declared dead.
            Assert.Equal(SiloStatus.Dead, this.oracle.GetApproximateSiloStatus(unknownSilo1));
            
            // Query for another unknown silo using a different mechanism.
            var unknownSilo2 = SiloAddress.New(new IPEndPoint(IPAddress.Parse("2.2.2.2"), 4), 4);
            Assert.False(this.oracle.IsFunctionalDirectory(unknownSilo2));

            // Report a new silo with the same endpoint as the unknown silo but a higher generation. 
            silos = new[]
            {
                silos[0],
                silos[1],
                CreateSiloInfo(
                    SiloAddress.New(new IPEndPoint(IPAddress.Parse("2.2.2.2"), 4), 5),
                    SiloAddress.Zero,
                    "TippyTop")
            };

            this.resolver.Notify(silos);
            listener.WaitForVersion(6);
            Assert.True(this.oracle.IsDeadSilo(unknownSilo2));
        }

        private class MockSiloDetails : ILocalSiloDetails
        {
            public string Name { get; set; }
            public string ClusterId { get; }
            public string DnsHostName { get; }
            public SiloAddress SiloAddress { get; set; }
            public SiloAddress GatewayAddress { get; }
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
            private readonly AutoResetEvent versionUpdated = new AutoResetEvent(false);
            public Dictionary<SiloAddress, SiloStatus> Silos { get; } = new Dictionary<SiloAddress, SiloStatus>();
            public List<Tuple<SiloAddress, SiloStatus>> Notifications { get; } = new List<Tuple<SiloAddress, SiloStatus>>();
            private int Version => this.Notifications.Count;

            public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
            {
                if (this.Silos.TryGetValue(updatedSilo, out var existingStatus))
                {
                    if (existingStatus == status) throw new InvalidOperationException($"Silo {updatedSilo} already has status {status}.");
                }
                this.Silos[updatedSilo] = status;
                this.Notifications.Add(Tuple.Create(updatedSilo, status));
                this.versionUpdated.Set();
            }

            public void WaitForVersion(int version)
            {
                while (version > this.Version)
                {
                    // If we are at the correct version, exit without waiting.
                    if (this.Version >= version)
                    {
                        break;
                    }

                    // Wait to be pulsed by an incoming update.
                    this.versionUpdated.WaitOne(TimeSpan.FromSeconds(1));
                }

                // Wake up any waiters to check the current version.
                this.versionUpdated.Set();
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
                return Task.CompletedTask;
            }

            public void Notify(FabricSiloInfo[] update)
            {
                foreach (var handler in this.Handlers) handler.OnUpdate(new[] {new ServicePartitionSilos(null, update.ToList())});
            }
        }
    }
}
*/