using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.General
{
    [DeploymentItem("Config_BootstrapProviders.xml")]
    [TestClass]
    public class BootstrapProvidersTests : UnitTestSiloHost
    {
        private static readonly TestingSiloOptions testOptions = new TestingSiloOptions
        {
            SiloConfigFile = new FileInfo("Config_BootstrapProviders.xml"),
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
        };

        public BootstrapProvidersTests()
            : base(testOptions)
        { }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Providers"), TestCategory("Silo")]
        public void BootstrapProvider_SiloStartsOk()
        {
            string providerName = "bootstrap1";
            MockBootstrapProvider bootstrapProvider = FindBootstrapProvider(providerName);
            Assert.IsNotNull(bootstrapProvider, "Found bootstrap provider {0}", providerName);
            Assert.AreEqual(1, bootstrapProvider.InitCount, "Init count");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Providers"), TestCategory("Silo")]
        public async Task BootstrapProvider_GrainCall()
        {
            string providerName = "bootstrap2";
            GrainCallBootstrapper bootstrapProvider = (GrainCallBootstrapper) FindBootstrapProvider(providerName);
            Assert.IsNotNull(bootstrapProvider, "Found bootstrap provider {0}", providerName);
            Assert.AreEqual(1, bootstrapProvider.InitCount, "Init count");

            long grainId = GrainCallBootstrapTestConstants.GrainId;
            int a = GrainCallBootstrapTestConstants.A;
            int b = GrainCallBootstrapTestConstants.B;
            ISimpleGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(grainId, SimpleGrain.SimpleGrainNamePrefix);
            int axb = await grain.GetAxB();
            Assert.AreEqual((a * b), axb, "Returned value from {0}", grainId);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Providers"), TestCategory("Silo")]
        public async Task BootstrapProvider_LocalGrainInit()
        {
            for (int i = 0; i < 20; i++ )
            {
                ITestContentGrain grain = GrainClient.GrainFactory.GetGrain<ITestContentGrain>(i);
                object content = await grain.FetchContentFromLocalGrain();
                logger.Info(content.ToString());
                string testGrainSiloId = await grain.GetRuntimeInstanceId();
                Assert.AreEqual(testGrainSiloId, content);
            }
        }

        private MockBootstrapProvider FindBootstrapProvider(string providerName)
        {
            MockBootstrapProvider providerInUse = null;
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                MockBootstrapProvider provider = (MockBootstrapProvider)siloHandle.Silo.TestHook.GetBootstrapProvider(providerName);
                Assert.IsNotNull(provider, "No storage provider found: Name={0} Silo={1}", providerName, siloHandle.Silo.SiloAddress);
                providerInUse = provider;
            }
            if (providerInUse == null)
            {
                Assert.Fail("Cannot find active storage provider currently in use, Name={0}", providerName);
            }
            return providerInUse;
        }
    }

    public class MockBootstrapProvider : MarshalByRefObject, IBootstrapProvider
    {
        private int initCount;

        public string Name { get; private set; }
        protected Logger logger { get; private set; }

        public int InitCount
        {
            get { return initCount; }
        }

        public virtual Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            this.Name = name;
            this.logger = providerRuntime.GetLogger(this.GetType().Name);
            logger.Info("Init Name={0}", name);
            Interlocked.Increment(ref initCount);
            return TaskDone.Done;
        }

        public Task Close()
        {
            return TaskDone.Done;
        }
    }

    internal static class GrainCallBootstrapTestConstants
    {
        internal const int A = 2;
        internal const int B = 3;
        internal const long GrainId = 12345;
    }

    public class GrainCallBootstrapper : MockBootstrapProvider
    {
        public override async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            await base.Init(name, providerRuntime, config);

            long grainId = GrainCallBootstrapTestConstants.GrainId;
            int a = GrainCallBootstrapTestConstants.A;
            int b = GrainCallBootstrapTestConstants.B;
            ISimpleGrain grain = providerRuntime.GrainFactory.GetGrain<ISimpleGrain>(grainId, SimpleGrain.SimpleGrainNamePrefix);
            logger.Info("Setting A={0} on {1}", a, grainId);
            await grain.SetA(a);
            logger.Info("Setting B={0} on {1}", b, grainId);
            await grain.SetB(b);
            logger.Info("Getting AxB from {0}", grainId);
            int axb = await grain.GetAxB();
            logger.Info("Got AxB={0} from {1}", axb, grainId);
            Assert.AreEqual((a * b), axb, "Value returned to {0} by {1}", this.GetType().Name, grainId);
        }
    }


    public class LocalGrainInitBootstrapper : IBootstrapProvider
    {
        public string Name { get; private set; }
        private Logger logger;

        public virtual async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            this.Name = name;
            this.logger = providerRuntime.GetLogger(this.GetType().Name);
            logger.Info("Init Name={0}", name);

            ILocalContentGrain grain = providerRuntime.GrainFactory.GetGrain<ILocalContentGrain>(Guid.NewGuid());
            // issue any grain call to activate this grain.
            await grain.Init();
            logger.Info("Finished Init Name={0}", name);
        }

        public Task Close()
        {
            return TaskDone.Done;
        }
    }
}
