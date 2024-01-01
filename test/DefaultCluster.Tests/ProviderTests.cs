using Xunit;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;

namespace DefaultCluster.Tests
{
    public class ProviderTests : OrleansTestingBase, IClassFixture<ProviderTests.Fixture>
    {
        private readonly Fixture fixture;

        public ProviderTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<Configurator>();
            }

            private class Configurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddGrainExtension<IAutoExtension, AutoExtension>();
                }
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Providers")]
        public async Task Providers_TestExtensions()
        {
            IExtensionTestGrain grain = this.fixture.GrainFactory.GetGrain<IExtensionTestGrain>(GetRandomGrainId());
            ITestExtension extension = grain.AsReference<ITestExtension>();
            bool exceptionThrown = true;

            try
            {
                var p1 = extension.CheckExtension_1();
                await p1;
                exceptionThrown = false;
            }
            catch (GrainExtensionNotInstalledException)
            {
                // This is the expected behavior
            }
            catch (AggregateException ex)
            {
                var baseEx = ex.GetBaseException();
                if (baseEx is GrainExtensionNotInstalledException)
                {
                    // This is also the expected behavior
                }
                else
                {
                    Assert.Fail("Incorrect exception thrown when an unimplemented method is invoked: " + baseEx);
                }
            }
            catch (Exception ex1)
            {
                Assert.Fail("Incorrect exception thrown when an unimplemented method is invoked: " + ex1);
            }

            if (!exceptionThrown)
            {
                Assert.Fail("Expected exception not thrown when no extension configured");
            }

            var p2 = grain.InstallExtension("test");
            await p2;

            try
            {
                var p1 = extension.CheckExtension_1();
                await p1;
                Assert.Equal("test", await p1);
            }
            catch (Exception exc)
            {
                Assert.Fail("Unexpected exception thrown when extension is configured. Exc = " + exc);
            }

            try
            {
                var p1 = extension.CheckExtension_2();
                await p1;
                Assert.Equal("23", await p1);
            }
            catch (Exception exc)
            {
                Assert.Fail("Unexpected exception thrown when extension is configured. Exc = " + exc);
            }
        }

        [Fact, TestCategory("Providers"), TestCategory("BVT"), TestCategory("Cast"), TestCategory("Generics")]
        public async Task Providers_ActivateNonGenericExtensionOfGenericInterface()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IGenericGrainWithNonGenericExtension<int>>(GetRandomGrainId());
            var extension = grain.AsReference<ISimpleExtension>(); //generic base grain not yet activated - virt refs only

            try
            {
                var res = await extension.CheckExtension_1(); //now activation occurs, but with non-generic reference
            }
            catch (Exception ex)
            {
                Assert.Fail("No exception should have been thrown. Ex: " + ex.Message);
            }

            Assert.True(true);
        }

        [Fact, TestCategory("Providers"), TestCategory("BVT"), TestCategory("Cast"), TestCategory("Generics")]
        public async Task Providers_ReferenceNonGenericExtensionOfGenericInterface() {
            var grain = this.fixture.GrainFactory.GetGrain<IGenericGrainWithNonGenericExtension<int>>(GetRandomGrainId());
            await grain.DoSomething(); //original generic grain activates here

            var extension = grain.AsReference<ISimpleExtension>();
            try {
                var res = await extension.CheckExtension_1();
            }
            catch(Exception ex) {
                Assert.Fail("No exception should have been thrown. Ex: " + ex.Message);
            }

            Assert.True(true);
        }

        [Fact, TestCategory("BVT"), TestCategory("Providers")]
        public async Task Providers_AutoInstallExtensionTest()
        {
            INoOpTestGrain grain = this.fixture.GrainFactory.GetGrain<INoOpTestGrain>(GetRandomGrainId());
            ISimpleExtension uninstalled = grain.AsReference<ISimpleExtension>();
            IAutoExtension autoInstalled = grain.AsReference<IAutoExtension>();
            await Assert.ThrowsAsync<GrainExtensionNotInstalledException>(() => uninstalled.CheckExtension_1());
            await autoInstalled.CheckExtension();
        }
    }
}