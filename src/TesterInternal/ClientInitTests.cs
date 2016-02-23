using System;
using System.Collections.Generic;
using System.Net;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Xunit;
using UnitTests.Tester;

namespace UnitTests
{
    public class ClientInitTests : HostedTestClusterEnsureDefaultStarted
    {
        public ClientInitTests()
        {
            if (!GrainClient.IsInitialized)
            {
                GrainClient.Initialize("ClientConfigurationForTesting.xml");
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_IsInitialized()
        {
            // First initialize will have been done by orleans unit test base class

            Assert.IsTrue(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_Uninitialize()
        {
            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_UnThenReinitialize()
        {
            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);

            GrainClient.Initialize("ClientConfigurationForTesting.xml");
            Assert.IsTrue(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_MultiInitialize()
        {
            // First initialize will have been done by orleans unit test base class

            GrainClient.Initialize("ClientConfigurationForTesting.xml");
            Assert.IsTrue(GrainClient.IsInitialized);

            GrainClient.Initialize("ClientConfigurationForTesting.xml");
            Assert.IsTrue(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_ErrorDuringInitialize()
        {
            ClientConfiguration cfg = new ClientConfiguration
            {
                TraceFileName = "TestOnlyThrowExceptionDuringInit.log",
                Gateways = new List<IPEndPoint>
                {
                    new IPEndPoint(IPAddress.Loopback, 40000)                        
                },
            };

            // First initialize will have been done by orleans unit test base class, so uninitialize back to null state
            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized, "GrainClient.IsInitialized");
            Assert.IsFalse(TraceLogger.IsInitialized, "Logger.IsInitialized");

            try
            {
                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = true;
                try
                {
                    GrainClient.Initialize(cfg);
                    Assert.Fail("Expected to get exception during GrainClient.Initialize when TestOnlyThrowExceptionDuringInit=true");
                }
                catch (Exception exc)
                {
                    Console.WriteLine("Expected to get exception during GrainClient.Initialize: {0}", exc);
                }
                Assert.IsFalse(GrainClient.IsInitialized, "GrainClient.IsInitialized");
                Assert.IsFalse(TraceLogger.IsInitialized, "Logger.IsInitialized");

                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = false;

                GrainClient.Initialize(cfg);
                Assert.IsTrue(GrainClient.IsInitialized, "GrainClient.IsInitialized");
                Assert.IsTrue(TraceLogger.IsInitialized, "Logger.IsInitialized");
            }
            finally
            {
                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = false;
            }
        }
        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_InitializeUnThenReInit()
        {
            GrainClient.Initialize("ClientConfigurationForTesting.xml");
            Assert.IsTrue(GrainClient.IsInitialized);

            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);

            GrainClient.Initialize("ClientConfigurationForTesting.xml");
            Assert.IsTrue(GrainClient.IsInitialized);

            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);
        }
    }
}
