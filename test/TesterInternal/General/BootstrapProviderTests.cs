using System;
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
                options.ClusterConfiguration.Globals.RegisterBootstrapProvider<UnitTests.General.MockBootstrapProvider>(BootstrapProviderName1);
                options.ClusterConfiguration.Globals.RegisterBootstrapProvider<UnitTests.General.GrainCallBootstrapper>(BootstrapProviderName2);
                options.ClusterConfiguration.Globals.RegisterBootstrapProvider<UnitTests.General.LocalGrainInitBootstrapper>(BootstrapProviderName3);
                options.ClusterConfiguration.Globals.RegisterBootstrapProvider<UnitTests.General.ControllableBootstrapProvider>(BootstrapProviderName4);

                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore", numStorageGrains: 1);
                options.ClusterConfiguration.AddMemoryStorageProvider("Default", numStorageGrains: 1);

                return new TestCluster(options);
            }
        }
        const string BootstrapProviderName1 = "bootstrap1";
        const string BootstrapProviderName2 = "bootstrap2";
        const string BootstrapProviderName3 = "bootstrap3";
        const string BootstrapProviderName4 = "bootstrap4";
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
            string providerName = BootstrapProviderName1;
            bool canGetBootstrapProvider = await CanFindBootstrapProviderInUse(providerName);
            Assert.True(canGetBootstrapProvider);
            int initCount = await GetInitCountForBootstrapProviderInUse(providerName);
            Assert.Equal(1, initCount); // Init count
        }

        [Fact, TestCategory("Functional"), TestCategory("Providers"), TestCategory("Bootstrap")]
        public async Task BootstrapProvider_GrainCall()
        {
            string providerName = BootstrapProviderName2;
            bool canGetBootstrapProvider = await CanFindBootstrapProviderInUse(providerName);
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
            for (int i = 0; i < 20; i++ )
            {
                ITestContentGrain grain = this.GrainFactory.GetGrain<ITestContentGrain>(i);
                object content = await grain.FetchContentFromLocalGrain();
                this.fixture.Logger.Info(content.ToString());
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
            string controllerName = BootstrapProviderName4;
            // check all providers are registered correctly
            foreach (SiloHandle silo in silos)
            {
                var providers = await this.HostedCluster.Client.GetTestHooks(silo).GetAllSiloProviderNames();

                Assert.Contains(BootstrapProviderName1, providers);
                Assert.Contains(BootstrapProviderName2, providers);
                Assert.Contains(BootstrapProviderName3, providers);
                Assert.Contains(BootstrapProviderName4, providers);
            }

            string args = "OneSetOfArgs";
            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);

            object[] replies = await mgmtGrain.SendControlCommandToProvider(controllerType, 
               controllerName, (int)ControllableBootstrapProvider.Commands.EchoArg, args);

            output.WriteLine("Got {0} replies {1}", replies.Length, Utils.EnumerableToString(replies));
            Assert.Equal(numSilos, replies.Length);  //  "Expected to get {0} replies to command {1}", numSilos, command
            Assert.True(replies.All(reply => reply.ToString().Equals(args)), $"Got args {args}");

            args = "DifferentSetOfArgs";
            replies = await mgmtGrain.SendControlCommandToProvider(controllerType,
                controllerName, (int)ControllableBootstrapProvider.Commands.EchoArg, args);

            output.WriteLine("Got {0} replies {1}", replies.Length, Utils.EnumerableToString(replies));
            Assert.Equal(numSilos, replies.Length);  //  "Expected to get {0} replies to command {1}", numSilos, command
            Assert.True(replies.All(reply => reply.ToString().Equals(args)), $"Got args {args}");
        }

        private async Task<bool> CanFindBootstrapProviderInUse(string providerName)
        {
            List<SiloHandle> silos = HostedCluster.GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                bool re = await this.HostedCluster.Client.GetTestHooks(siloHandle).HasBoostraperProvider(providerName);
                if (re)
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<int> GetInitCountForBootstrapProviderInUse(string providerName)
        {
            var mgmt = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            // request provider InitCount on all silos in this cluster
            object[] results = await mgmt.SendControlCommandToProvider(typeof(GrainCallBootstrapper).FullName, BootstrapProviderName2, (int)MockBootstrapProvider.Commands.InitCount, null);
            foreach (var re in results)
            {
                int initCountOnThisProviderInThisSilo = (int) re;
                if ((int)initCountOnThisProviderInThisSilo > 0)
                    return initCountOnThisProviderInThisSilo;
            }
            return -1;
        }
    }
}
