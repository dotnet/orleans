#if !NETCOREAPP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.Utilities;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using TestExtensions;
using TestVersionGrainInterfaces;
using TestVersionGrains;
using Xunit;

namespace Tester.HeterogeneousSilosTests.UpgradeTests
{
    public abstract class UpgradeTestsBase : IDisposable, IAsyncLifetime
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);

        private TimeSpan waitDelay;

        protected IClusterClient Client => this.cluster.Client;
        protected IManagementGrain ManagementGrain => this.cluster.Client.GetGrain<IManagementGrain>(0);
#if DEBUG
        private const string BuildConfiguration = "Debug";
#else
        private const string BuildConfiguration = "Release";
#endif
        private const string CommonParentDirectory = "test";
        private const string BinDirectory = "bin";
        private const string VersionsProjectDirectory = "Grains";
        private const string GrainsV1ProjectName = "TestVersionGrains";
        private const string GrainsV2ProjectName = "TestVersionGrains2";
        private const string VersionTestBinaryName = "TestVersionGrains.dll";
        private readonly DirectoryInfo assemblyGrainsV1Dir;
        private readonly DirectoryInfo assemblyGrainsV2Dir;
        
        private readonly List<SiloHandle> deployedSilos = new List<SiloHandle>();
        private int siloIdx = 0;
        private TestClusterBuilder builder;
        private TestCluster cluster;

        protected abstract Type VersionSelectorStrategy { get; }

        protected abstract Type CompatibilityStrategy { get; }

        protected virtual short SiloCount => 2;

        protected UpgradeTestsBase()
        {
            var testDirectory = new DirectoryInfo(GetType().Assembly.Location);

            while (String.Compare(testDirectory.Name, CommonParentDirectory, StringComparison.OrdinalIgnoreCase) != 0 || testDirectory.Parent == null)
            {
                testDirectory = testDirectory.Parent;
            }

            if (testDirectory.Parent == null)
            {
                throw new InvalidOperationException($"Cannot locate 'test' directory starting from '{GetType().Assembly.Location}'");
            }

            assemblyGrainsV1Dir = GetVersionTestDirectory(testDirectory, GrainsV1ProjectName);
            assemblyGrainsV2Dir = GetVersionTestDirectory(testDirectory, GrainsV2ProjectName);
        }

        private DirectoryInfo GetVersionTestDirectory(DirectoryInfo testDirectory, string directoryName)
        {
            var projectDirectory = Path.Combine(testDirectory.FullName, VersionsProjectDirectory, directoryName, BinDirectory);

            var directories = Directory.GetDirectories(projectDirectory, BuildConfiguration, SearchOption.AllDirectories);

            if (directories.Length != 1)
            {
                throw new InvalidOperationException($"Number of directories found for pattern: '{BuildConfiguration}' under {testDirectory.FullName}: {directories.Length}");
            }

            var files = Directory.GetFiles(directories[0], VersionTestBinaryName, SearchOption.AllDirectories);

            if (files.Length != 1)
            {
                throw new InvalidOperationException($"Number of files found for pattern: '{VersionTestBinaryName}' under {testDirectory.FullName}: {files.Length}");
            }

            return new DirectoryInfo(Path.GetDirectoryName(files[0]));
        }

        protected async Task Step1_StartV1Silo_Step2_StartV2Silo_Step3_StopV2Silo(int step2Version)
        {
            const int numberOfGrains = 100;

            await StartSiloV1();

            // Only V1 exist for now
            for (var i = 0; i < numberOfGrains; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }

            // Start a new silo with V2
            var siloV2 = await StartSiloV2();

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
            await StopSilo(siloV2);

            // Now all activation should be V1
            for (var i = 0; i < numberOfGrains * 3; i++)
            {
                var grain = Client.GetGrain<IVersionUpgradeTestGrain>(i);
                Assert.Equal(1, await grain.GetVersion());
            }
        }

        protected async Task ProxyCallNoPendingRequest(int expectedVersion)
        {
            await StartSiloV1();

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
            await StartSiloV1();

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

        protected async Task<SiloHandle> StartSiloV1()
        {
            var handle = await StartSilo(assemblyGrainsV1Dir);
            await Task.Delay(waitDelay);
            return handle;
        }

        protected async Task<SiloHandle> StartSiloV2()
        {
            var handle = await StartSilo(assemblyGrainsV2Dir);
            await Task.Delay(waitDelay);
            return handle;
        }

        private async Task<SiloHandle> StartSilo(DirectoryInfo rootDir)
        {
            SiloHandle silo;
            if (this.siloIdx == 0)
            {
                // Setup configuration
                this.builder = new TestClusterBuilder(1)
                {
                    CreateSiloAsync = AppDomainSiloHandle.Create
                };
                TestDefaultConfiguration.ConfigureTestCluster(this.builder);
                builder.Options.ApplicationBaseDirectory = rootDir.FullName;
                builder.AddSiloBuilderConfigurator<VersionGrainsSiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<VersionGrainsClientConfigurator>();
                builder.Properties[nameof(SiloCount)] = this.SiloCount.ToString();
                builder.Properties[nameof(RefreshInterval)] = RefreshInterval.ToString();
                builder.Properties[nameof(VersionSelectorStrategy)] = this.VersionSelectorStrategy.Name;
                builder.Properties[nameof(CompatibilityStrategy)] = this.CompatibilityStrategy.Name;
                waitDelay = TestCluster.GetLivenessStabilizationTime(new ClusterMembershipOptions(), didKill: false);

                this.cluster = builder.Build();
                await this.cluster.DeployAsync();
                silo = this.cluster.Primary;
            }
            else
            {
                var configBuilder = new ConfigurationBuilder();
                foreach (var source in cluster.ConfigurationSources) configBuilder.Add(source);
                var testClusterOptions = new TestClusterOptions();
                configBuilder.Build().Bind(testClusterOptions);

                // Override the root directory.
                var sources = new IConfigurationSource[]
                {
                    new MemoryConfigurationSource {InitialData = new Dictionary<string, string>{ [nameof(TestClusterOptions.ApplicationBaseDirectory)] = rootDir.FullName } }
                };

                silo = await TestCluster.StartSiloAsync(cluster, siloIdx, testClusterOptions, sources);
            }

            this.deployedSilos.Add(silo);
            this.siloIdx++;

            return silo;
        }

        protected async Task StopSilo(SiloHandle handle)
        {
            await handle?.StopSiloAsync(true);
            this.deployedSilos.Remove(handle);
            await Task.Delay(waitDelay);
        }

        public void Dispose()
        {
            if (!deployedSilos.Any()) return;

            var primarySilo = this.deployedSilos[0];
            foreach (var silo in this.deployedSilos.Skip(1))
            {
                silo.Dispose();
            }
            primarySilo.Dispose();
            this.Client?.Dispose();
        }

        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            var primarySilo = this.deployedSilos[0];
            foreach (var silo in this.deployedSilos.Skip(1))
            {
                await silo.StopSiloAsync(true);
                silo.Dispose();
            }

            await primarySilo.StopSiloAsync(true);
            primarySilo.Dispose();
            if (this.Client != null) await this.Client.Close();
            this.Client?.Dispose();
        }
    }
}
#endif