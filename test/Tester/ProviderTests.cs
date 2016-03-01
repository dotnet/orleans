﻿using System;
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
    }
}
