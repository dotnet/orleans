using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    public class BootstrapProvidersTests : OrleansTestingBase, IClassFixture<BootstrapProvidersTests.Fixture>
    {
        private readonly ITestOutputHelper output;

        public class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    SiloConfigFile = new FileInfo("Config_BootstrapProviders.xml"),
                    StartFreshOrleans = true,
                    StartPrimary = true,
                    StartSecondary = true,
                },
                new TestingClientOptions()
                {
                    ClientConfigFile = new FileInfo("ClientConfigurationForTesting.xml")
                });
            }
        }

        protected TestingSiloHost HostedCluster { get; private set; }

        public BootstrapProvidersTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.HostedCluster = fixture.HostedCluster;
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
            ISimpleGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(grainId, SimpleGrain.SimpleGrainNamePrefix);
            int axb = await grain.GetAxB();
            Assert.Equal((a * b), axb);
        }

        [Fact, TestCategory("Functional"), TestCategory("Providers"), TestCategory("Bootstrap")]
        public async Task BootstrapProvider_LocalGrainInit()
        {
            for (int i = 0; i < 20; i++ )
            {
                ITestContentGrain grain = GrainClient.GrainFactory.GetGrain<ITestContentGrain>(i);
                object content = await grain.FetchContentFromLocalGrain();
                logger.Info(content.ToString());
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
                IList<IBootstrapProvider> providers = silo.Silo.BootstrapProviders;
                output.WriteLine("Found {0} bootstrap providers in silo {1}: {2}", 
                    providers.Count, silo.Name, Utils.EnumerableToString(
                        providers.Select(pr => pr.Name + "=" + pr.GetType().FullName)));

                Assert.Equal(4, providers.Count); // Found correct number of bootstrap providers
                
                Assert.True(providers.Any(bp => bp.Name.Equals(controllerName)), "Name found");
                Assert.True(providers.Any(bp => bp.GetType().FullName.Equals(controllerType)), "Typefound");
            }

            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);

            object[] replies = await mgmtGrain.SendControlCommandToProvider(controllerType, controllerName, command, args);

            output.WriteLine("Got {0} replies {1}", replies.Length, Utils.EnumerableToString(replies));
            Assert.Equal(numSilos,  replies.Length);  //  "Expected to get {0} replies to command {1}", numSilos, command
            Assert.True(replies.All(reply => reply.ToString().Equals(command.ToString())), $"Got command {command}");

            command += 1;
            replies = await mgmtGrain.SendControlCommandToProvider(controllerType, controllerName, command, args);

            output.WriteLine("Got {0} replies {1}", replies.Length, Utils.EnumerableToString(replies));
            Assert.Equal(numSilos,  replies.Length);  //  "Expected to get {0} replies to command {1}", numSilos, command
            Assert.True(replies.All(reply => reply.ToString().Equals(command.ToString())), $"Got command {command}");
        }

        private MockBootstrapProvider FindBootstrapProvider(string providerName)
        {
            MockBootstrapProvider providerInUse = null;
            List<SiloHandle> silos = HostedCluster.GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                MockBootstrapProvider provider = (MockBootstrapProvider)siloHandle.Silo.TestHook.GetBootstrapProvider(providerName);
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
