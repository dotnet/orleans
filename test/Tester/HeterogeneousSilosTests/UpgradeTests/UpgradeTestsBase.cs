using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using TestVersionGrainInterfaces;
using Xunit;

namespace Tester.HeterogeneousSilosTests.UpgradeTests
{
    public abstract class UpgradeTestsBase : IDisposable
    {
        private readonly TimeSpan refreshInterval = TimeSpan.FromMilliseconds(200);
        private TimeSpan waitDelay;
        protected IClusterClient Client { get; private set; }
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
        private TestClusterOptions options;

        protected abstract VersionSelectorStrategy VersionSelectorStrategy { get; }

        protected abstract CompatibilityStrategy CompatibilityStrategy { get; }

        protected UpgradeTestsBase()
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
        }

        protected async Task Step1_StartV1Silo_Step2_StartV2Silo_Step3_StopV2Silo(int step2Version)
        {
            const int numberOfGrains = 100;

            await DeployCluster();

            // Only V1 exist for now
            for (var i = 0; i < numberOfGrains; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }

            // Start a new silo with V2
            await StartSiloV2();

            for (var i = 0; i < numberOfGrains; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }

            for (var i = numberOfGrains; i < numberOfGrains * 2; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(step2Version, await grain.GetVersion());
            }

            // Stop the V2 silo
            await StopSiloV2();

            // Now all activation should be V1
            for (var i = 0; i < numberOfGrains * 3; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }
        }

        protected async Task ProxyCallNoPendingRequest(int expectedVersion)
        {
            await DeployCluster();

            // Only V1 exist for now
            var grain0 = Client.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await grain0.GetVersion());

            await StartSiloV2();

            // New activation should be V2
            var grain1 = Client.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await grain1.GetVersion());

            Assert.Equal(expectedVersion, await grain1.ProxyGetVersion(grain0));
            Assert.Equal(expectedVersion, await grain0.GetVersion());
        }

        protected async Task ProxyCallWithPendingRequest(int expectedVersion)
        {
            await DeployCluster();

            // Only V1 exist for now
            var grain0 = Client.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await grain0.GetVersion());

            // Start a new silo with V2
            await StartSiloV2();

            // New activation should be V2
            var grain1 = Client.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await grain1.GetVersion());

            var waitingTask = grain0.LongRunningTask(TimeSpan.FromSeconds(5));
            var callBeforeUpgrade = grain0.GetVersion();
            await Task.Delay(100); // Make sure requests are not sent out of order
            var callProvokingUpgrade = grain1.ProxyGetVersion(grain0);

            await waitingTask;
            Assert.Equal(1, await callBeforeUpgrade);
            Assert.Equal(expectedVersion, await callProvokingUpgrade);
        }

        protected async Task DeployCluster()
        {
            this.options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.AssumeHomogenousSilosForTesting = false;
            options.ClusterConfiguration.Globals.TypeMapRefreshInterval = refreshInterval;
            options.ClusterConfiguration.Globals.DefaultVersionSelectorStrategy = VersionSelectorStrategy;
            options.ClusterConfiguration.Globals.DefaultCompatibilityStrategy = CompatibilityStrategy;
            options.ClientConfiguration.Gateways.RemoveAt(1); // Only use primary gw

            waitDelay = TestCluster.GetLivenessStabilizationTime(options.ClusterConfiguration.Globals, false);

            await StartSiloV1();
            Client = new ClientBuilder().UseConfiguration(options.ClientConfiguration).Build();
            await Client.Connect();
        }

        private async Task StartSiloV1()
        {
            this.siloV1 = StartSilo(Silo.PrimarySiloName, assemblyGrainsV1Dir);
            await Task.Delay(waitDelay);
        }

        protected async Task StartSiloV2()
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

        protected async Task StopSiloV2()
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
            Client?.Dispose();
        }
    }
}