using System.Net;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    /// <summary>
    /// Tests for grain placement behavior when cluster topology changes.
    /// </summary>
    public sealed class GrainPlacementClusterChangeTests(ITestOutputHelper output) : TestClusterPerTest
    {
        [Theory]
        [InlineData("Primary")]
        [InlineData("Secondary")]
        [TestCategory("BVT"), TestCategory("Placement")]
        public async Task PreferLocalPlacementGrain_ShouldMigrateWhenHostSiloKilled(string value)
        {
            foreach (SiloHandle silo in HostedCluster.GetActiveSilos())
            {
                output.WriteLine(
                    "Silo {0} : Address = {1} Proxy gateway: {2}",
                    silo.Name, silo.SiloAddress, silo.GatewayAddress);
            }

            IPEndPoint targetSilo;
            if (value == "Primary")
            {
                targetSilo = HostedCluster.Primary.SiloAddress.Endpoint;
            }
            else
            {
                targetSilo = HostedCluster.SecondarySilos.First().SiloAddress.Endpoint;
            }

            Guid proxyKey;
            IRandomPlacementTestGrain proxy;
            IPEndPoint expected;
            do
            {
                proxyKey = Guid.NewGuid();
                proxy = GrainFactory.GetGrain<IRandomPlacementTestGrain>(proxyKey);
                expected = await proxy.GetEndpoint();
            } while (!targetSilo.Equals(expected));
            output.WriteLine("Proxy grain was originally located on silo {0}", expected);

            Guid grainKey = proxyKey;
            await proxy.StartPreferLocalGrain(grainKey);
            IPreferLocalPlacementTestGrain grain = GrainFactory.GetGrain<IPreferLocalPlacementTestGrain>(grainKey);
            IPEndPoint actual = await grain.GetEndpoint();
            output.WriteLine("PreferLocalPlacement grain was originally located on silo {0}", actual);
            Assert.Equal(expected, actual);  // "PreferLocalPlacement strategy should create activations on the local silo."

            SiloHandle siloToKill = HostedCluster.GetActiveSilos().First(s => s.SiloAddress.Endpoint.Equals(expected));
            output.WriteLine("Killing silo {0} hosting locally placed grain", siloToKill);
            await HostedCluster.StopSiloAsync(siloToKill);

            IPEndPoint newActual = await grain.GetEndpoint();
            output.WriteLine("PreferLocalPlacement grain was recreated on silo {0}", newActual);
            Assert.NotEqual(expected, newActual);  // "PreferLocalPlacement strategy should recreate activations on other silo if local fails."
        }
    }
}
