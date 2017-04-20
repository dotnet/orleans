using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestVersionGrainInterfaces;
using Xunit;

namespace Tester.HeterogeneousSilosTests
{
    [TestCategory("ExcludeXAML")]
    public class UpgradeTests : IDisposable
    {

        private readonly TimeSpan refreshInterval = TimeSpan.FromMilliseconds(200);
        private readonly TimeSpan waitDelay;
        private readonly IClusterClient client;
        private IGrainFactory grainFactory => client;

#if DEBUG
        private const string BuildConfiguration = "Debug";
#else
        private const string BuildConfiguration = "Release";
#endif
        private const string AssemblyGrainsV1Vs = @"..\..\..\Versions\TestVersionGrains\bin\" + BuildConfiguration;
        private const string AssemblyGrainsV2Vs = @"..\..\..\Versions\TestVersionGrains2\bin\" + BuildConfiguration;
        private const string AssemblyGrainsV1Build = @"TestVersionGrainsV1";
        private const string AssemblyGrainsV2Build = @"TestVersionGrainsV2";
        private readonly DirectoryInfo assemblyGrainsV1Dir;
        private readonly DirectoryInfo assemblyGrainsV2Dir;

        private SiloHandle siloV1;
        private SiloHandle siloV2;
        private readonly TestClusterOptions options;

        public UpgradeTests()
        {
            // Setup dll references
            // If test run from cmdlime
            if (Directory.Exists(AssemblyGrainsV1Build))
            {
                assemblyGrainsV1Dir = new DirectoryInfo(AssemblyGrainsV1Build);
                assemblyGrainsV2Dir = new DirectoryInfo(AssemblyGrainsV2Build);
            }
            // If test run from VS
            else
            {
                // If not run from vnext
                if (Directory.Exists(AssemblyGrainsV1Vs))
                {
                    assemblyGrainsV1Dir = new DirectoryInfo(AssemblyGrainsV1Vs);
                    assemblyGrainsV2Dir = new DirectoryInfo(AssemblyGrainsV2Vs);
                }
                else
                {
                    // vnext
                    var target = @"\net462";
                    assemblyGrainsV1Dir = new DirectoryInfo(@"..\" + AssemblyGrainsV1Vs + target);
                    assemblyGrainsV2Dir = new DirectoryInfo(@"..\" + AssemblyGrainsV2Vs + target);
                }
            }

            this.options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.AssumeHomogenousSilosForTesting = false;
            options.ClusterConfiguration.Globals.TypeMapRefreshInterval = refreshInterval;
            options.ClientConfiguration.Gateways.RemoveAt(1); // Only use primary gw

            waitDelay = TestCluster.GetLivenessStabilizationTime(options.ClusterConfiguration.Globals, false);

            StartSiloV1();
            client = new ClientBuilder().UseConfiguration(options.ClientConfiguration).Build();
            client.Connect().Wait();
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional")]
        public async Task AlwaysCreateNewActivationWithLatestVersionTest()
        {
            const int numberOfGrains = 100;

            // Only V1 exist for now
            for (var i = 0; i < numberOfGrains; i++)
            {
                var grain = grainFactory.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion()); 
            }

            // Start a new silo with V2
            await StartSiloV2();

            // New activation should be V2
            for (var i = 0; i < numberOfGrains * 2; i++)
            {
                var grain = grainFactory.GetGrain<IVersionUpgradeTestGrain>(i);
                var expectedVersion = i < numberOfGrains ? 1 : 2;
                Assert.Equal(expectedVersion, await grain.GetVersion());
            }

            // Stop the V2 silo
            await StopSiloV2();

            // Now all activation should be V1
            for (var i = 0; i < numberOfGrains * 3; i++)
            {
                var grain = grainFactory.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional")]
        public async Task UpgradeNoPendingRequestTest()
        {
            // Only V1 exist for now
            var grain0 = grainFactory.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await grain0.GetVersion());

            // Start a new silo with V2
            await StartSiloV2();

            // New activation should be V2
            var grain1 = grainFactory.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await grain1.GetVersion());

            // First call should provoke "upgrade" of grain0
            Assert.Equal(2, await grain1.ProxyGetVersion(grain0));
            Assert.Equal(2, await grain0.GetVersion());

            // Stop the V2 silo
            await StopSiloV2();

            // Now all activation should be V1
            Assert.Equal(1, await grain0.GetVersion());
            Assert.Equal(1, await grain1.GetVersion());
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional")]
        public async Task UpgradeSeveralQueuedRequestsTest()
        {
            // Only V1 exist for now
            var grain0 = grainFactory.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await grain0.GetVersion());

            // Start a new silo with V2
            await StartSiloV2();

            // New activation should be V2
            var grain1 = grainFactory.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await grain1.GetVersion());

            var waitingTask = grain0.LongRunningTask(TimeSpan.FromSeconds(5));
            var callBeforeUpgrade = grain0.GetVersion();
            await Task.Delay(100); // Make sure requests are not sent out of order
            var callProvokingUpgrade = grain1.ProxyGetVersion(grain0);

            await waitingTask;
            Assert.Equal(1, await callBeforeUpgrade);
            Assert.Equal(2, await callProvokingUpgrade);
        }

        private void StartSiloV1()
        {
            this.siloV1 = StartSilo(Silo.PrimarySiloName, assemblyGrainsV1Dir);
        }

        private async Task StartSiloV2()
        {
            this.siloV2 = StartSilo("Secondary_1", assemblyGrainsV2Dir);
            await Task.Delay(waitDelay);
        }

        private SiloHandle StartSilo(string name, DirectoryInfo rootDir)
        {
            var siloType = (name == Silo.PrimarySiloName) ? Silo.SiloType.Primary : Silo.SiloType.Secondary;
            var silo = AppDomainSiloHandle.Create(
                name, 
                siloType, 
                options.ClusterConfiguration,
                options.ClusterConfiguration.Overrides[name], 
                new Dictionary<string, GeneratedAssembly>(),
                rootDir.FullName);
            return silo;
        }

        private async Task StopSiloV2()
        {
            StopSilo(siloV2);
            await Task.Delay(waitDelay);
        }

        private void StopSilo(SiloHandle handle)
        {
            handle?.StopSilo(true);
        }

        public void Dispose()
        {
            siloV2?.Dispose();
            siloV1?.Dispose();
            client?.Dispose();
        }
    }
}
