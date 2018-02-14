using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.ServiceFabric;
using Orleans.Configuration;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace TestServiceFabric
{
    /// <summary>
    /// Tests for <see cref="UnknownSiloMonitor"/>.
    /// </summary>
    [TestCategory("ServiceFabric"), TestCategory("BVT")]
    public class UnknownSiloMonitorTests
    {
        private readonly ITestOutputHelper output;

        public UnknownSiloMonitorTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private ILogger<UnknownSiloMonitor> Logger => new TestOutputLogger<UnknownSiloMonitor>(this.output);

        /// <summary>
        /// Tests that unknown silos are declared dead after a configured period of time.
        /// </summary>
        /// <param name="singletonPartition">
        /// Whether or not the service is running in a singleton Service Fabric partition.
        /// </param>
        [Fact]
        public void SiloEventuallyBecomesDead()
        {
            var now = new[] { DateTime.UtcNow };
            var options = new ServiceFabricClusteringOptions();
            var monitor = new UnknownSiloMonitor(new OptionsWrapper<ServiceFabricClusteringOptions>(options), this.Logger)
            {
                GetDateTime = () => now[0]
            };

            var knownSilos = new Dictionary<SiloAddress, SiloStatus>
            {
                [NewSiloAddress("2.2.2.2", 9030, 2)] = SiloStatus.Active
            };

            // Report a silo as having an unknown status.
            var deadSilo = NewSiloAddress("1.1.1.1", 9030, 1000);
            monitor.ReportUnknownSilo(deadSilo);

            // No time has passed and the set of known silos contains no information about the reported silo,
            // therefore we expect no reports even after running multiple times.
            Assert.Empty(monitor.DetermineDeadSilos(knownSilos));
            Assert.Empty(monitor.DetermineDeadSilos(knownSilos));

            // Advance passed the expiration time.
            now[0] += options.UnknownSiloRemovalPeriod + TimeSpan.FromMilliseconds(1);

            // The silo should be declared dead by the monitor.
            Assert.Contains(deadSilo, monitor.DetermineDeadSilos(knownSilos));
        }

        /// <summary>
        /// Tests that unknown silos are declared dead when a silo supersedes it on the same endpoint.
        /// </summary>
        /// <param name="singletonPartition">
        /// Whether or not the service is running in a singleton Service Fabric partition.
        /// </param>
        [Fact]
        public void SiloDeclaredDeadWhenSupersededOnSameEndpoint()
        {
            var now = new[] { DateTime.UtcNow };
            var options = new ServiceFabricClusteringOptions();
            var monitor = new UnknownSiloMonitor(new OptionsWrapper<ServiceFabricClusteringOptions>(options), this.Logger)
            {
                GetDateTime = () => now[0]
            };

            var knownSilos = new Dictionary<SiloAddress, SiloStatus>();

            // Report a silo as having an unknown status.
            var deadSilo = NewSiloAddress("1.1.1.1", 9030, 1000);
            monitor.ReportUnknownSilo(deadSilo);
            Assert.Empty(monitor.DetermineDeadSilos(knownSilos));

            // Create a silo with the same IP and port but a lower generation.
            var predecessorSilo = NewSiloAddress("1.1.1.1", 9030, 500);
            knownSilos[predecessorSilo] = SiloStatus.Active;
            Assert.Empty(monitor.DetermineDeadSilos(knownSilos));

            knownSilos[predecessorSilo] = SiloStatus.Dead;
            Assert.Empty(monitor.DetermineDeadSilos(knownSilos));

            // Create a silo with the same IP and port but a higher generation.
            var supersedingSilo = NewSiloAddress("1.1.1.1", 9030, 2000);

            // A status of None is equivalent to no status, so no declarations should be made.
            knownSilos[supersedingSilo] = SiloStatus.None;
            Assert.Empty(monitor.DetermineDeadSilos(knownSilos));

            // The silo should be declared dead by the monitor even if the newer silo is dead.
            knownSilos[supersedingSilo] = SiloStatus.Dead;
            Assert.Contains(deadSilo, monitor.DetermineDeadSilos(knownSilos));

            // The silo should be declared dead by the monitor.
            knownSilos[supersedingSilo] = SiloStatus.Active;
            Assert.Contains(deadSilo, monitor.DetermineDeadSilos(knownSilos));
        }

        /// <summary>
        /// Tests that unknown silos are declared dead when a silo supersedes it on the same IP address but different port if
        /// the service has a singleton partition.
        /// </summary>
        [Fact]
        public void SiloNotDeclaredDeadWhenSupersededOnSameAddressDifferentPort()
        {
            var now = new[] { DateTime.UtcNow };
            var options = new ServiceFabricClusteringOptions();
            var monitor = new UnknownSiloMonitor(new OptionsWrapper<ServiceFabricClusteringOptions>(options), this.Logger)
            {
                GetDateTime = () => now[0]
            };

            var deadSilo = NewSiloAddress("1.1.1.1", 1111, 1000);
            var supersedingSilo = NewSiloAddress("1.1.1.1", 9999, 2000);
            var knownSilos = new Dictionary<SiloAddress, SiloStatus>
            {
                [supersedingSilo] = SiloStatus.Active
            };

            // Report a silo as having an unknown status.
            monitor.ReportUnknownSilo(deadSilo);

            // The silo is not declared dead.
            Assert.Empty(monitor.DetermineDeadSilos(knownSilos));
        }

        private static SiloAddress NewSiloAddress(string ip, ushort port, int generation) =>
            SiloAddress.New(new IPEndPoint(IPAddress.Parse(ip), port), generation);
    }
}