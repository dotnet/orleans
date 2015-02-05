using Microsoft.VisualStudio.TestTools.UnitTesting;

#if DEBUG || REVISIT

namespace UnitTests.General
{
#if REVISIT || DEBUG

    [TestClass]
    public class LocalCallsTests : UnitTestBase
    {
        public LocalCallsTests()
            : base(true)
        {
        }

        //[TestCleanup]
        //public void Cleanup()
        //{
        //    //ResetAllAdditionalRuntimes();
        //    //ResetDefaultRuntimes();
        //}

        [TestMethod, TestCategory("Revisit"), TestCategory("LocalCalls")]
        public void LocalRetry()
        {
            //TimeSpan timeout = TimeSpan.FromMilliseconds(5000);

            //IProxyErrorGrain client = ProxyErrorGrainFactory.CreateGrain(Strategies: new[] { GrainStrategy.PartitionPlacement(0, 2) });

            //IErrorGrain reference = ErrorGrainFactory.CreateGrain(Strategies: new[] { GrainStrategy.PartitionPlacement(1, 2) });

            ////string hostId = client.GetRuntimeInstanceId().GetValue(timeout);
            ////string[] parts = hostId.Split(':');
            ////Assert.AreEqual("32445", parts[1]);
            //client.ConnectTo(reference).Wait(timeout);

            //client.SetA(1).Wait();

            //StartAdditionalOrleans();
            //client.SetA(2).Wait(timeout);
        }

        [TestMethod, TestCategory("Revisit"), TestCategory("LocalCalls"), TestCategory("ErrorHandling")]
        public void LocalError()
        {
            //TimeSpan timeout = TimeSpan.FromMilliseconds(15000);

            //IProxyErrorGrain client = ProxyErrorGrainFactory.CreateGrain(Strategies: new[] { GrainStrategy.PartitionPlacement(0, 2) });

            //IErrorGrain reference = ErrorGrainFactory.CreateGrain(Strategies: new[] { GrainStrategy.PartitionPlacement(1, 2) });
            //((GrainReference)reference).Wait();

            ////string hostId = client.GetRuntimeInstanceId().GetValue(timeout);
            ////string[] parts = hostId.Split(':');
            ////Assert.AreEqual("32445", parts[1]);
            //client.ConnectTo(reference).Wait(timeout);

            //client.SetA(1).Wait();

            //StartAdditionalOrleans();
            //try
            //{
            //    client.SetAError(2).Wait(timeout);
            //}
            //catch (Exception exc)
            //{
            //    Console.WriteLine(Logger.PrintException(exc));

            //    Exception e = exc.GetBaseException();
            //    if (e is TimeoutException)
            //        Assert.Fail("Timeout exception thrown instead of the expected error exception.");
            //    else if (e.Message != "SetAError-Exception")
            //    {
            //        Assert.Fail(String.Format("Unexpected exception: {0}", e));
            //    }
            //}
        }
    }
#endif
}
#endif
