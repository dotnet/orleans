using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests
{
    /// <summary>
    /// Summary description for ErrorHandlingGrainTest
    /// </summary>
    [TestClass]
    public class TimeoutTests : UnitTestSiloHost
    {
        private TimeSpan originalTimeout;

        public TimeoutTests()
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestCleanup]
        public void Cleanup()
        {
            //RuntimeClient.Current.SetResponseTimeout(originalTimeout);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Timeout")]
        public void Timeout_LongMethod()
        {
            originalTimeout = RuntimeClient.Current.GetResponseTimeout();
            bool finished = false;
            var grainName = typeof (ErrorGrain).FullName;
            IErrorGrain grain = GrainClient.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainName);
            TimeSpan timeout = TimeSpan.FromMilliseconds(1000);
            RuntimeClient.Current.SetResponseTimeout(timeout);

            Task promise = grain.LongMethod((int)timeout.Multiply(4).TotalMilliseconds);
            //promise = grain.LongMethodWithError(2000);

            // there is a race in the test here. If run in debugger, the invocation can actually finish OK
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            try
            {
                finished = promise.Wait(timeout.Multiply(3));
                Assert.Fail("Should have thrown");
            }
            catch (Exception exc)
            {
                stopwatch.Stop();
                Exception baseExc = exc.GetBaseException();
                if (!(baseExc is TimeoutException))
                {
                    Assert.Fail("Should not have got here " + exc);
                }
            }
            Console.WriteLine("Waited for " + stopwatch.Elapsed);
            Assert.IsTrue(!finished);
            Assert.IsTrue(stopwatch.Elapsed >= timeout.Multiply(0.9), "Waited less than " + timeout.Multiply(0.9) + ". Waited " + stopwatch.Elapsed);
            Assert.IsTrue(stopwatch.Elapsed <= timeout.Multiply(2), "Waited longer than " + timeout.Multiply(2) + ". Waited " + stopwatch.Elapsed);
            Assert.IsTrue(promise.Status == TaskStatus.Faulted);

            // try to re-use the promise and should fail immideately.
            try
            {
                stopwatch = new Stopwatch();
                promise.Wait();
                Assert.Fail("Should have thrown");
            }
            catch (Exception exc)
            {
                stopwatch.Stop();
                Exception baseExc = exc.GetBaseException();
                if (!(baseExc is TimeoutException))
                {
                    Assert.Fail("Should not have got here " + exc);
                }
            }
            Assert.IsTrue(stopwatch.Elapsed <= timeout.Multiply(0.1), "Waited longer than " + timeout.Multiply(0.1) + ". Waited " + stopwatch.Elapsed);
            Assert.IsTrue(promise.Status == TaskStatus.Faulted);
        }
    }
}
