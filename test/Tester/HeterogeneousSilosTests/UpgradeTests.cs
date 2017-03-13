#if !NETSTANDARD_TODO
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Orleans;
using Orleans.TestingHost;
using TestExtensions;
using TestVersionGrainInterfaces;
using Xunit;

namespace Tester.HeterogeneousSilosTests
{
    public class UpgradeTests : TestClusterPerTest
    {
        private readonly TimeSpan refreshInterval = TimeSpan.FromMilliseconds(200);

#if DEBUG
        private const string BuildConfiguration = "Debug";
#else
        private const string BuildConfiguration = "Release";
#endif
        private const string AssemblyIfaceDirV1Vs = @"..\..\..\Versions\TestVersionGrainInterfaces\bin\" + BuildConfiguration;
        private const string AssemblyIfaceDirV2Vs = @"..\..\..\Versions\TestVersionGrainInterfaces2\bin\" + BuildConfiguration;
        private const string AssemblyGrainsV1Vs = @"..\..\..\Versions\TestVersionGrains\bin\" + BuildConfiguration;
        private const string AssemblyGrainsV2Vs = @"..\..\..\Versions\TestVersionGrains2\bin\" + BuildConfiguration;
        private const string AssemblyIfaceDirV1Build = @"TestVersionGrainInterfacesV1";
        private const string AssemblyIfaceDirV2Build = @"TestVersionGrainInterfacesV2";
        private const string AssemblyGrainsV1Build = @"TestVersionGrainsV1";
        private const string AssemblyGrainsV2Build = @"TestVersionGrainsV2";
        private DirectoryInfo assemblyIfaceV1Dir;
        private DirectoryInfo assemblyIfaceV2Dir;
        private DirectoryInfo assemblyGrainsV1Dir;
        private DirectoryInfo assemblyGrainsV2Dir;

        public override TestCluster CreateTestCluster()
        {
            // Setup dll references
            // If test run from cmdlime
            if (Directory.Exists(AssemblyIfaceDirV1Build))
            {
                assemblyIfaceV1Dir = new DirectoryInfo(AssemblyIfaceDirV1Build);
                assemblyIfaceV2Dir = new DirectoryInfo(AssemblyIfaceDirV2Build);
                assemblyGrainsV1Dir = new DirectoryInfo(AssemblyGrainsV1Build);
                assemblyGrainsV2Dir = new DirectoryInfo(AssemblyGrainsV2Build);
            }
            // If test run from VS
            else
            {
                assemblyIfaceV1Dir = new DirectoryInfo(AssemblyIfaceDirV1Vs);
                assemblyIfaceV2Dir = new DirectoryInfo(AssemblyIfaceDirV2Vs);
                assemblyGrainsV1Dir = new DirectoryInfo(AssemblyGrainsV1Vs);
                assemblyGrainsV2Dir = new DirectoryInfo(AssemblyGrainsV2Vs);
            }

            // We cannot copy the reference to the iface at build because it would be automatically loaded
            // by the test silos, so we need do do this to load the reference from a subfolder
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                if (!args.Name.Contains("TestVersionGrainInterfaces")) return null;

                var dllPath = Path.Combine(assemblyIfaceV1Dir.FullName, "TestVersionGrainInterfaces.dll");
                var assembly = Assembly.LoadFile(dllPath);
                return assembly;
            };

            var options = new TestClusterOptions(1);
            options.ClusterConfiguration.Globals.AssumeHomogenousSilosForTesting = false;
            options.ClusterConfiguration.Globals.TypeMapRefreshInterval = refreshInterval;
            options.ClusterConfiguration.Overrides["Primary"].AdditionalAssemblyDirectories.Add(assemblyGrainsV1Dir.FullName, SearchOption.TopDirectoryOnly);
            return new TestCluster(options);
        }

        [Fact, TestCategory("BVT")]
        public async Task AlwaysCreateNewActivationWithLatestVersionTest()
        {
            const int numberOfGrains = 100;

            // Only V1 exist for now
            for (var i = 0; i < numberOfGrains; i++)
            {
                var grain = GrainFactory.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion()); 
            }

            // Start a new silo with V2
            var siloV2 = await StartSiloWithV2();

            // New activation should be V2
            for (var i = numberOfGrains; i < numberOfGrains * 2; i++)
            {
                var grain = GrainFactory.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(2, await grain.GetVersion());
            }

            // Stop the V2 silo
            HostedCluster.StopSilo(siloV2);
            await Task.Delay(1000);

            // Now all activation should be V1
            for (var i = numberOfGrains * 2; i < numberOfGrains * 3; i++)
            {
                var grain = GrainFactory.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }
        }

        [Fact, TestCategory("BVT")]
        public async Task UpgradeNoPendingRequestTest()
        {
            // Only V1 exist for now
            var grain0 = GrainFactory.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await grain0.GetVersion());

            // Start a new silo with V2
            var siloV2 = await StartSiloWithV2();

            // New activation should be V2
            var grain1 = GrainFactory.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await grain1.GetVersion());

            // First call should provoke "upgrade" of grain0
            Assert.Equal(2, await grain1.ProxyGetVersion(grain0));
            Assert.Equal(2, await grain0.GetVersion());

            // Stop the V2 silo
            HostedCluster.StopSilo(siloV2);
            await Task.Delay(1000);

            // Now all activation should be V1
            Assert.Equal(1, await grain0.GetVersion());
            Assert.Equal(1, await grain1.GetVersion());
        }

        [Fact, TestCategory("BVT")]
        public async Task UpgradeSeveralQueuedRequestsTest()
        {
            // Only V1 exist for now
            var grain0 = GrainFactory.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await grain0.GetVersion());

            // Start a new silo with V2
            await StartSiloWithV2();

            // New activation should be V2
            var grain1 = GrainFactory.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await grain1.GetVersion());

            var waitingTask = grain0.LongRunningTask(TimeSpan.FromSeconds(5));
            var callBeforeUpgrade = grain0.GetVersion();
            var callProvokingUpgrade = grain1.ProxyGetVersion(grain0);

            await waitingTask;
            Assert.Equal(1, await callBeforeUpgrade);
            Assert.Equal(2, await callProvokingUpgrade);
        }

        private async Task<SiloHandle> StartSiloWithV2()
        {
            var handle = HostedCluster.StartAdditionalSilo(configuration =>
            {
                configuration.AdditionalAssemblyDirectories.Clear();
                configuration.AdditionalAssemblyDirectories.Add(
                    assemblyGrainsV2Dir.FullName, SearchOption.TopDirectoryOnly);
            });

            await Task.Delay(1000);

            return handle;
        }
    }
}
#endif