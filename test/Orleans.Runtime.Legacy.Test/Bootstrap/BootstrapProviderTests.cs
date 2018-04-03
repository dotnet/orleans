using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost.Utils;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using TestExtensions;

namespace UnitTests.General
{
    public class BootstrapProvidersTests : OrleansTestingBase, IClassFixture<BootstrapProvidersTests.Fixture>
    {
        private readonly ITestOutputHelper output;
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.RegisterBootstrapProvider<MockBootstrapProvider>(MockBootstrapProviderName);
                    legacy.ClusterConfiguration.Globals.RegisterBootstrapProvider<GrainCallBootstrapper>(GrainCallBootstrapperName);
                    legacy.ClusterConfiguration.Globals.RegisterBootstrapProvider<LocalGrainInitBootstrapper>(LocalGrainInitBootstrapperName);
                    legacy.ClusterConfiguration.Globals.RegisterBootstrapProvider<ControllableBootstrapProvider>(ControllableBootstrapProviderName);
                });
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }

            public class SiloConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.AddMemoryGrainStorageAsDefault(ops => ops.NumStorageGrains = 1)
                        .AddMemoryGrainStorage("MemoryStore", ops=>ops.NumStorageGrains = 1);
                }
            }
        }

        const string MockBootstrapProviderName = "bootstrap1";
        const string GrainCallBootstrapperName = "bootstrap2";
        const string LocalGrainInitBootstrapperName = "bootstrap3";
        const string ControllableBootstrapProviderName = "bootstrap4";

        protected TestCluster HostedCluster => this.fixture.HostedCluster;
        protected IGrainFactory GrainFactory => this.fixture.GrainFactory;

        public BootstrapProvidersTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Providers"), TestCategory("Bootstrap")]
        public async void BootstrapProvider_SiloStartsOk()
        {
            string providerName = MockBootstrapProviderName;
            bool canGetBootstrapProvider = await CanFindBootstrapProviderInUse<MockBootstrapProvider>(providerName);
            Assert.True(canGetBootstrapProvider);
            int initCount = await GetInitCountForBootstrapProviderInUse(providerName);
            Assert.Equal(1, initCount); // Init count
        }

        [Fact, TestCategory("Functional"), TestCategory("Providers"), TestCategory("Bootstrap")]
        public async Task BootstrapProvider_GrainCall()
        {
            string providerName = GrainCallBootstrapperName;
            bool canGetBootstrapProvider = await CanFindBootstrapProviderInUse<GrainCallBootstrapper>(providerName);
            Assert.True(canGetBootstrapProvider);
            int initCount = await GetInitCountForBootstrapProviderInUse(providerName);
            Assert.Equal(1, initCount); // Init count
            long grainId = GrainCallBootstrapTestConstants.GrainId;
            int a = GrainCallBootstrapTestConstants.A;
            int b = GrainCallBootstrapTestConstants.B;
            ISimpleGrain grain = this.GrainFactory.GetGrain<ISimpleGrain>(grainId, SimpleGrain.SimpleGrainNamePrefix);
            int axb = await grain.GetAxB();
            Assert.Equal((a * b), axb);
        }

        [Fact, TestCategory("Functional"), TestCategory("Providers"), TestCategory("Bootstrap")]
        public async Task BootstrapProvider_LocalGrainInit()
        {
            for (int i = 0; i < 20; i++)
            {
                ITestContentGrain grain = this.GrainFactory.GetGrain<ITestContentGrain>(i);
                object content = await grain.FetchContentFromLocalGrain();
                this.fixture.Logger.Info(content.ToString());
                string testGrainSiloId = await grain.GetRuntimeInstanceId();
                Assert.Equal(testGrainSiloId, content);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Providers"), TestCategory("Bootstrap")]
        public async Task BootstrapProvider_RegisteredCorrectly()
        {
            SiloHandle[] silos = HostedCluster.GetActiveSilos().ToArray();
            int numSilos = silos.Length;
            // check all providers are registered correctly
            foreach (SiloHandle silo in silos)
            {
                int count = await GetSiloCountForProvider<MockBootstrapProvider>(MockBootstrapProviderName);
                Assert.Equal(numSilos, count);
                count = await GetSiloCountForProvider<GrainCallBootstrapper>(GrainCallBootstrapperName);
                Assert.Equal(numSilos, count);
                count = await GetSiloCountForProvider<LocalGrainInitBootstrapper>(LocalGrainInitBootstrapperName);
                Assert.Equal(numSilos, count);
                count = await GetSiloCountForProvider<ControllableBootstrapProvider>(ControllableBootstrapProviderName);
                Assert.Equal(numSilos, count);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Providers"), TestCategory("Bootstrap")]
        public async Task BootstrapProvider_Controllable()
        {
            string controllerName = ControllableBootstrapProviderName;
            string controllerType = typeof(ControllableBootstrapProvider).FullName;
            SiloHandle[] silos = HostedCluster.GetActiveSilos().ToArray();
            int numSilos = silos.Length;

            string args = "OneSetOfArgs";
            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);

            object[] replies = await mgmtGrain.SendControlCommandToProvider(controllerType,
                controllerName,
                (int) ControllableBootstrapProvider.Commands.EchoArg,
                args);

            output.WriteLine("Got {0} replies {1}", replies.Length, Utils.EnumerableToString(replies));
            Assert.Equal(numSilos, replies.Length); //  "Expected to get {0} replies to command {1}", numSilos, command
            Assert.True(replies.All(reply => reply.ToString().Equals(args)), $"Got args {args}");

            args = "DifferentSetOfArgs";
            replies = await mgmtGrain.SendControlCommandToProvider(controllerType,
                controllerName,
                (int) ControllableBootstrapProvider.Commands.EchoArg,
                args);

            output.WriteLine("Got {0} replies {1}", replies.Length, Utils.EnumerableToString(replies));
            Assert.Equal(numSilos, replies.Length); //  "Expected to get {0} replies to command {1}", numSilos, command
            Assert.True(replies.All(reply => reply.ToString().Equals(args)), $"Got args {args}");
        }

        private async Task<bool> CanFindBootstrapProviderInUse<T>(string providerName)
        {
            List<SiloHandle> silos = HostedCluster.GetActiveSilos().ToList();
            int siloCount = await GetSiloCountForProvider<T>(providerName);
            return siloCount == silos.Count;
        }

        private async Task<int> GetInitCountForBootstrapProviderInUse(string providerName)
        {
            var mgmt = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            // request provider InitCount on all silos in this cluster
            object[] results = await mgmt.SendControlCommandToProvider(typeof(GrainCallBootstrapper).FullName,
                GrainCallBootstrapperName,
                (int) MockBootstrapProvider.Commands.InitCount,
                null);
            foreach (var re in results)
            {
                int initCountOnThisProviderInThisSilo = (int) re;
                if ((int) initCountOnThisProviderInThisSilo > 0)
                    return initCountOnThisProviderInThisSilo;
            }

            return -1;
        }

        private async Task<int> GetAllSiloProviderNames(string providerName)
        {
            var mgmt = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            // request provider InitCount on all silos in this cluster
            object[] results = await mgmt.SendControlCommandToProvider(typeof(GrainCallBootstrapper).FullName,
                GrainCallBootstrapperName,
                (int) MockBootstrapProvider.Commands.InitCount,
                null);
            foreach (var re in results)
            {
                int initCountOnThisProviderInThisSilo = (int) re;
                if ((int) initCountOnThisProviderInThisSilo > 0)
                    return initCountOnThisProviderInThisSilo;
            }

            return -1;
        }

        /// <summary>
        /// Returns the number of silos the provider is configured on.
        /// </summary>
        private async Task<int> GetSiloCountForProvider<T>(string providerName)
        {
            var mgmt = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            // request provider InitCount on all silos in this cluster
            object[] results = await mgmt.SendControlCommandToProvider(typeof(T).FullName, providerName, (int) MockBootstrapProvider.Commands.QueryName, null);
            return results.Cast<string>().Count(name => name == providerName);
        }
    }
}