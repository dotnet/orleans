using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTestGrainInterfaces;

namespace UnitTests
{
    [TestClass]
    public class ProviderTests : UnitTestBase
    {
        public ProviderTests()
            : base(true)
        {
        }

        //public ProviderTests(int dummy)
        //    : base(new Options { StartPrimary = true, StartSecondary = false, StartClient = true })
        //{
        //}

        [ClassCleanup()]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Providers")]
        public void Providers_TestExtensions()
        {
            IExtensionTestGrain grain = ExtensionTestGrainFactory.GetGrain(1);
            ITestExtension extension = TestExtensionFactory.Cast(grain);
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

        // TODO: [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Providers")]
        [TestMethod, TestCategory("Failures"), TestCategory("Providers")]
        public void Providers_GenericExtensions()
        {
            IGenericExtensionTestGrain<string> grain = GenericExtensionTestGrainFactory<string>.GetGrain(2);
            IGenericTestExtension<string> extension = GenericTestExtensionFactory<string>.Cast(grain);

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
                Assert.Fail("Unexpected exception thrown when extension is configured. Exc = " + exc.ToString());
            }

            try
            {
                var p1 = extension.CheckExtension_2();
                p1.Wait();
                Assert.AreEqual("23", p1.Result, "Extension value not set correctly");
            }
            catch (Exception exc)
            {
                Assert.Fail("Unexpected exception thrown when extension is configured. Exc = " + exc.ToString());
            }
        }
    }
}
