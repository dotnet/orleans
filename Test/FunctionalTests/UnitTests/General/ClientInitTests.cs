using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;


namespace UnitTests
{
    [TestClass]
    public class ClientInitTests : UnitTestBase
    {
        public ClientInitTests()
            : base(new Options { StartSecondary = false })
        {
        }

        [TestCleanup]
        public void TestCleanup()
        {
            ResetAllAdditionalRuntimes();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Client")]
        public void ClientInit_IsInitialized()
        {
            // First initialize will have been done by orleans unit test base class

            Assert.IsTrue(GrainClient.IsInitialized);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Client")]
        public void ClientInit_Uninitialize()
        {
            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Client")]
        public void ClientInit_UnThenReinitialize()
        {
            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);

            GrainClient.Initialize();
            Assert.IsTrue(GrainClient.IsInitialized);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Client")]
        public void ClientInit_MultiInitialize()
        {
            // First initialize will have been done by orleans unit test base class

            GrainClient.Initialize();
            Assert.IsTrue(GrainClient.IsInitialized);

            GrainClient.Initialize();
            Assert.IsTrue(GrainClient.IsInitialized);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Client")]
        public void ClientInit_ErrorDuringInitialize()
        {
            ClientConfiguration cfg = new ClientConfiguration
            {
                TraceFileName = "TestOnlyThrowExceptionDuringInit.log",
                Gateways = new List<IPEndPoint>
                {
                    new IPEndPoint(IPAddress.Loopback, 30000)                        
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
        [TestMethod, TestCategory("Nightly"), TestCategory("Client")]
        public void ClientInit_InitializeUnThenReInit()
        {
            GrainClient.Initialize();
            Assert.IsTrue(GrainClient.IsInitialized);

            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);

            GrainClient.Initialize();
            Assert.IsTrue(GrainClient.IsInitialized);

            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);
        }


        [TestMethod, TestCategory("Revisit"), TestCategory("Client")]
        //[ExpectedException(typeof(InvalidOperationException))]
        public void ClientInit_TryToCreateGrainWhenUninitialized()
        {
            //Client.Uninitialize();
            //Assert.IsFalse(Client.IsInitialized);
            //ISimpleGrain grain = SimpleGrainFactory.GetGrain(GetRandomGrainId());
            //grain.Wait();
            //Assert.Fail("CreateGrain should have failed before this point");
        }
    }
}
