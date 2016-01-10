using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

#pragma warning disable 618

namespace UnitTests.General
{
    [TestClass]
    public class BasicActivationTests : HostedTestClusterEnsureDefaultStarted
    {
        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_ActivateAndUpdate()
        {
            long g1Key = GetRandomGrainId();
            long g2Key = GetRandomGrainId();
            ITestGrain g1 = GrainClient.GrainFactory.GetGrain<ITestGrain>(g1Key);
            ITestGrain g2 = GrainClient.GrainFactory.GetGrain<ITestGrain>(g2Key);
            Assert.AreEqual(g1Key, g1.GetPrimaryKeyLong());
            Assert.AreEqual(g1Key, g1.GetKey().Result);
            Assert.AreEqual(g1Key.ToString(), g1.GetLabel().Result);
            Assert.AreEqual(g2Key, g2.GetKey().Result);
            Assert.AreEqual(g2Key.ToString(), g2.GetLabel().Result);

            g1.SetLabel("one").Wait();
            Assert.AreEqual("one", g1.GetLabel().Result);
            Assert.AreEqual(g2Key.ToString(), g2.GetLabel().Result);

            ITestGrain g1a = GrainClient.GrainFactory.GetGrain<ITestGrain>(g1Key);
            Assert.AreEqual("one", g1a.GetLabel().Result);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_Guid_ActivateAndUpdate()
        {
            Guid guid1 = Guid.NewGuid();
            Guid guid2 = Guid.NewGuid();

            IGuidTestGrain g1 = GrainClient.GrainFactory.GetGrain<IGuidTestGrain>(guid1);
            IGuidTestGrain g2 = GrainClient.GrainFactory.GetGrain<IGuidTestGrain>(guid2);
            Assert.AreEqual(guid1, g1.GetPrimaryKey());
            Assert.AreEqual(guid1, g1.GetKey().Result);
            Assert.AreEqual(guid1.ToString(), g1.GetLabel().Result);
            Assert.AreEqual(guid2, g2.GetKey().Result);
            Assert.AreEqual(guid2.ToString(), g2.GetLabel().Result);

            g1.SetLabel("one").Wait();
            Assert.AreEqual("one", g1.GetLabel().Result);
            Assert.AreEqual(guid2.ToString(), g2.GetLabel().Result);

            IGuidTestGrain g1a = GrainClient.GrainFactory.GetGrain<IGuidTestGrain>(guid1);
            Assert.AreEqual("one", g1a.GetLabel().Result);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("ErrorHandling"), TestCategory("GetGrain")]
        public void BasicActivation_Fail()
        {
            bool failed;
            long key = 0;
            try
            {
                // Key values of -2 are not allowed in this case
                ITestGrain fail = GrainClient.GrainFactory.GetGrain<ITestGrain>(-2);
                key = fail.GetKey().Result;
                failed = false;
            }
            catch (Exception e)
            {
                Assert.IsInstanceOfType(e.GetBaseException(), typeof(OrleansException));
                failed = true;
            }

            if (!failed) Assert.Fail("Should have failed, but instead returned " + key);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_ULong_MaxValue()
        {
            ulong key1AsUlong = UInt64.MaxValue; // == -1L
            long key1 = (long)key1AsUlong;

            ITestGrain g1 = GrainClient.GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.AreEqual(key1, g1.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetKey().Result);
            Assert.AreEqual(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MaxValue").Wait();
            Assert.AreEqual("MaxValue", g1.GetLabel().Result);

            ITestGrain g1a = GrainClient.GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.AreEqual("MaxValue", g1a.GetLabel().Result);
            Assert.AreEqual(key1, g1a.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1a.GetKey().Result);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_ULong_MinValue()
        {
            ulong key1AsUlong = UInt64.MinValue; // == zero
            long key1 = (long)key1AsUlong;

            ITestGrain g1 = GrainClient.GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.AreEqual(key1, g1.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetKey().Result);
            Assert.AreEqual(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MinValue").Wait();
            Assert.AreEqual("MinValue", g1.GetLabel().Result);

            ITestGrain g1a = GrainClient.GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.AreEqual("MinValue", g1a.GetLabel().Result);
            Assert.AreEqual(key1, g1a.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1a.GetKey().Result);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_Long_MaxValue()
        {
            long key1 = Int32.MaxValue;
            ulong key1AsUlong = (ulong)key1;

            ITestGrain g1 = GrainClient.GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.AreEqual(key1, g1.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetKey().Result);
            Assert.AreEqual(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MaxValue").Wait();
            Assert.AreEqual("MaxValue", g1.GetLabel().Result);

            ITestGrain g1a = GrainClient.GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.AreEqual("MaxValue", g1a.GetLabel().Result);
            Assert.AreEqual(key1, g1a.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1a.GetKey().Result);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_Long_MinValue()
        {
            long key1 = Int64.MinValue;
            ulong key1AsUlong = (ulong)key1;

            ITestGrain g1 = GrainClient.GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.AreEqual((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetKey().Result);
            Assert.AreEqual(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MinValue").Wait();
            Assert.AreEqual("MinValue", g1.GetLabel().Result);

            ITestGrain g1a = GrainClient.GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.AreEqual("MinValue", g1a.GetLabel().Result);
            Assert.AreEqual(key1, g1a.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1a.GetKey().Result);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public void BasicActivation_MultipleGrainInterfaces()
        {
            ITestGrain simple = GrainClient.GrainFactory.GetGrain<ITestGrain>(GetRandomGrainId());

            simple.GetMultipleGrainInterfaces_List().Wait();
            logger.Info("GetMultipleGrainInterfaces_List() worked");

            simple.GetMultipleGrainInterfaces_Array().Wait();

            logger.Info("GetMultipleGrainInterfaces_Array() worked");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("Reentrancy")]
        public void BasicActivation_Reentrant_RecoveryAfterExpiredMessage()
        {
            List<Task> promises = new List<Task>();
            TimeSpan prevTimeout = GrainClient.GetResponseTimeout();

            // set short response time and ask to do long operation, to trigger expired msgs in the silo queues.
            TimeSpan shortTimeout = TimeSpan.FromMilliseconds(1000);
            GrainClient.SetResponseTimeout(shortTimeout);

            ITestGrain grain = GrainClient.GrainFactory.GetGrain<ITestGrain>(GetRandomGrainId());
            int num = 10;
            for (long i = 0; i < num; i++)
            {
                Task task = grain.DoLongAction(TimeSpan.FromMilliseconds(shortTimeout.TotalMilliseconds * 3), "A_" + i);
                promises.Add(task);
            }
            try
            {
                Task.WhenAll(promises).Wait();
            }catch(Exception)
            {
                logger.Info("Done with stress iteration.");
            }

            // wait a bit to make sure expired msgs in the silo is trigered.
            Thread.Sleep(TimeSpan.FromSeconds(10));

            // set the regular response time back, expect msgs ot succeed.
            GrainClient.SetResponseTimeout(prevTimeout);

            logger.Info("About to send a next legit request that should succeed.");
            grain.DoLongAction(TimeSpan.FromMilliseconds(1), "B_" + 0).Wait();
            logger.Info("The request succeeded.");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("RequestContext"), TestCategory("GetGrain")]
        public void BasicActivation_TestRequestContext()
        {
            ITestGrain g1 = GrainClient.GrainFactory.GetGrain<ITestGrain>(GetRandomGrainId());
            Task<Tuple<string, string>> promise1 = g1.TestRequestContext();
            Tuple<string, string> requstContext = promise1.Result;
            logger.Info("Request Context is: " + requstContext);
            Assert.IsNotNull(requstContext.Item2, "Item2=" + requstContext.Item2);
            Assert.IsNotNull(requstContext.Item1, "Item1=" + requstContext.Item1);
        }
    }
}

#pragma warning restore 618
