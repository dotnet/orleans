using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Tester;

namespace UnitTests
{
    public class ProviderTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional"), TestCategory("Providers")]
        public void Providers_TestExtensions()
        {
            IExtensionTestGrain grain = GrainClient.GrainFactory.GetGrain<IExtensionTestGrain>(GetRandomGrainId());
            ITestExtension extension = grain.AsReference<ITestExtension>();
            bool exceptionThrown = true;

            try
            {
                var p1 = extension.CheckExtension_1();
                p1.Wait();
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
            p2.Wait();

            try
            {
                var p1 = extension.CheckExtension_1();
                p1.Wait();
                Assert.AreEqual("test", p1.Result, "Extension value not set correctly");
            }
            catch (Exception exc)
            {
                Assert.Fail("Unexpected exception thrown when extension is configured. Exc = " + exc);
            }

            try
            {
                var p1 = extension.CheckExtension_2();
                p1.Wait();
                Assert.AreEqual("23", p1.Result, "Extension value not set correctly");
            }
            catch (Exception exc)
            {
                Assert.Fail("Unexpected exception thrown when extension is configured. Exc = " + exc);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Providers"), TestCategory("BVT"), TestCategory("Cast"), TestCategory("Generics")]
        public async Task Providers_ActivateNonGenericExtensionOfGenericInterface()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IGenericGrainWithNonGenericExtension<int>>(GetRandomGrainId());
            var extension = grain.AsReference<ISimpleExtension>(); //generic base grain not yet activated - virt refs only

            try
            {
                var res = await extension.CheckExtension_1(); //now activation occurs, but with non-generic reference
            }
            catch (Exception ex)
            {
                Assert.Fail("No exception should have been thrown. Ex: {0}", ex.Message);
            }

            Assert.IsTrue(true);
        }

        [Fact, TestCategory("Functional"), TestCategory("Providers"), TestCategory("BVT"), TestCategory("Cast"), TestCategory("Generics")]
        public async Task Providers_ReferenceNonGenericExtensionOfGenericInterface() {
            var grain = GrainClient.GrainFactory.GetGrain<IGenericGrainWithNonGenericExtension<int>>(GetRandomGrainId());
            await grain.DoSomething(); //original generic grain activates here

            var extension = grain.AsReference<ISimpleExtension>();
            try {
                var res = await extension.CheckExtension_1();
            }
            catch(Exception ex) {
                Assert.Fail("No exception should have been thrown. Ex: {0}", ex.Message);
            }

            Assert.IsTrue(true);
        }
    }
}