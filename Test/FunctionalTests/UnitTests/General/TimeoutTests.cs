using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTestGrains;

namespace UnitTests
{
    /// <summary>
    /// Summary description for ErrorHandlingGrainTest
    /// </summary>
    [TestClass]
    public class TimeoutTests : UnitTestBase
    {
        private TimeSpan originalTimeout;

        public TimeoutTests()
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestCleanup]
        public void Cleanup()
        {
            //RuntimeClient.Current.SetResponseTimeout(originalTimeout);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Timeout")]
        public void Timeout_LongMethod()
        {
            originalTimeout = RuntimeClient.Current.GetResponseTimeout();
            bool finished = false;
            IErrorGrain grain = ErrorGrainFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.ErrorGrain");
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


        [TestMethod, TestCategory("Failures"), TestCategory("Timeout"), TestCategory("Silo")]
        public void Timeout_FailedSilo()
        {
            originalTimeout = RuntimeClient.Current.GetResponseTimeout();
            TimeSpan timeout = TimeSpan.FromSeconds(1);
            RuntimeClient.Current.SetResponseTimeout(timeout);
            IErrorGrain grain = ErrorGrainFactory.GetGrain(GetRandomGrainId(), "UnitTestGrains.ErrorGrain");

            Task promise = grain.SetA(2);
            promise.Wait();
            Console.WriteLine(grain.GetA().Result);
            grain.SetA(3).Wait();

            ResetAllAdditionalRuntimes();
            StopRuntime(Primary);
            StopRuntime(Secondary);

            Task<int> promiseValue = grain.GetA();
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            bool timeoutHappened = false;
            bool retryExceeded = false;
            try
            {
                int val = promiseValue.Result;
                Assert.Fail("Should have thrown " + val);
            }
            catch (Exception exc)
            {
                stopwatch.Stop();
                Exception baseExc = exc.GetBaseException();
                
                if (baseExc is TimeoutException)
                    timeoutHappened = true;
                if(baseExc is OrleansException)
                    if(baseExc.Message.StartsWith("Retry count exceeded"))
                        retryExceeded = true;

                if (!timeoutHappened && !retryExceeded)
                {
                    Assert.Fail("Should not have got here " + exc);
                }
                Console.WriteLine("Have thrown TimeoutException or Retry count exceeded correctly.");
            }
            if (timeoutHappened)
            {
                Assert.IsTrue(stopwatch.Elapsed >= timeout.Multiply(0.9), "Waited less than " + timeout.Multiply(0.9) + ". Waited " + stopwatch.Elapsed);
            }
            Assert.IsTrue(stopwatch.Elapsed <= timeout.Multiply(1.5), "Waited longer than " + timeout.Multiply(1.5) + ". Waited " + stopwatch.Elapsed);
        }
    }
}
