using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;
using Tester;
using Orleans.Runtime.Configuration;
using TestExtensions;

namespace UnitTests.General
{
    public class BootstrapProvidersTests : OrleansTestingBase, IClassFixture<BootstrapProvidersTests.Fixture>
    {
        private readonly ITestOutputHelper output;
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();
                options.ClusterConfiguration.Globals.RegisterBootstrapProvider<UnitTests.General.MockBootstrapProvider>("bootstrap1");
                options.ClusterConfiguration.Globals.RegisterBootstrapProvider<UnitTests.General.GrainCallBootstrapper>("bootstrap2");
                options.ClusterConfiguration.Globals.RegisterBootstrapProvider<UnitTests.General.LocalGrainInitBootstrapper>("bootstrap3");
                options.ClusterConfiguration.Globals.RegisterBootstrapProvider<UnitTests.General.ControllableBootstrapProvider>("bootstrap4");

                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore", numStorageGrains: 1);
                options.ClusterConfiguration.AddMemoryStorageProvider("Default", numStorageGrains: 1);

                return new TestCluster(options);
            }
        }

        protected TestCluster HostedCluster => this.fixture.HostedCluster;
        protected IGrainFactory GrainFactory => this.fixture.GrainFactory;

        public BootstrapProvidersTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Providers"), TestCategory("Bootstrap")]
        public void BootstrapProvider_SiloStartsOk()
        {
            string providerName = "bootstrap1";
            MockBootstrapProvider bootstrapProvider = FindBootstrapProvider(providerName);
            Assert.NotNull(bootstrapProvider);
            Assert.Equal(1, bootstrapProvider.InitCount); // Init count
        }

        [Fact, TestCategory("Functional"), TestCategory("Providers"), TestCategory("Bootstrap")]
        public async Task BootstrapProvider_GrainCall()
        {
            string providerName = "bootstrap2";
            GrainCallBootstrapper bootstrapProvider = (GrainCallBootstrapper) FindBootstrapProvider(providerName);
            Assert.NotNull(bootstrapProvider);
            Assert.Equal(1, bootstrapProvider.InitCount); // Init count

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
            for (int i = 0; i < 20; i++ )
            {
                ITestContentGrain grain = this.GrainFactory.GetGrain<ITestContentGrain>(i);
                object content = await grain.FetchContentFromLocalGrain();
                this.logger.Info(content.ToString());
                string testGrainSiloId = await grain.GetRuntimeInstanceId();
                Assert.Equal(testGrainSiloId, content);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Providers"), TestCategory("Bootstrap")]
        public async Task BootstrapProvider_Controllable()
        {
            SiloHandle[] silos = HostedCluster.GetActiveSilos().ToArray();
            int numSilos = silos.Length;

            string controllerType = typeof(ControllableBootstrapProvider).FullName;
            string controllerName = "bootstrap4";

            int command = 1;
            string args = "CommandArgs";

            foreach (SiloHandle silo in silos)
            {
                var providers = await silo.TestHook.GetAllSiloProviderNames();

                Assert.Contains("bootstrap1", providers);
                Assert.Contains("bootstrap2", providers);
                Assert.Contains("bootstrap3", providers);
                Assert.Contains("bootstrap4", providers);
            }

            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);

            object[] replies = await mgmtGrain.SendControlCommandToProvider(controllerType, controllerName, command, args);

            output.WriteLine("Got {0} replies {1}", replies.Length, Utils.EnumerableToString(replies));
            Assert.Equal(numSilos, replies.Length);  //  "Expected to get {0} replies to command {1}", numSilos, command
            Assert.True(replies.All(reply => reply.ToString().Equals(command.ToString())), $"Got command {command}");

            command += 1;
            replies = await mgmtGrain.SendControlCommandToProvider(controllerType, controllerName, command, args);

            output.WriteLine("Got {0} replies {1}", replies.Length, Utils.EnumerableToString(replies));
            Assert.Equal(numSilos, replies.Length);  //  "Expected to get {0} replies to command {1}", numSilos, command
            Assert.True(replies.All(reply => reply.ToString().Equals(command.ToString())), $"Got command {command}");
        }

        private MockBootstrapProvider FindBootstrapProvider(string providerName)
        {
            MockBootstrapProvider providerInUse = null;
            List<SiloHandle> silos = HostedCluster.GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                MockBootstrapProvider provider = (MockBootstrapProvider)siloHandle.AppDomainTestHook.GetBootstrapProvider(providerName);
                Assert.NotNull(provider);
                providerInUse = provider;
            }
            if (providerInUse == null)
            {
                Assert.True(false, $"Cannot find active storage provider currently in use, Name={providerName}");
            }
            return providerInUse;
        }
    }
}
