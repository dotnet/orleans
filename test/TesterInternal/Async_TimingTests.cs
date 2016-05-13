using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Xunit;

#pragma warning disable 618

namespace UnitTests
{
    public class Async_TimingTests
    {
        private readonly TraceLogger logger;

        public Async_TimingTests()
        {
            TraceLogger.Initialize(ClientConfiguration.StandardLoad());
            logger = TraceLogger.GetLogger("AC_TimingTests", TraceLogger.LoggerType.Application);
            logger.Info("----------------------------- STARTING AC_TimingTests -------------------------------------");
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Async_Task_WithTimeout_Wait()
        {
            TimeSpan timeout = TimeSpan.FromMilliseconds(2000);
            TimeSpan sleepTime = TimeSpan.FromMilliseconds(4000);
            TimeSpan delta = TimeSpan.FromMilliseconds(200);
            Stopwatch watch = new Stopwatch();
            watch.Start();

            Task<int> promise = Task<int>.Factory.StartNew(() =>
                {
                    Thread.Sleep(sleepTime);
                    return 5;
                }).WithTimeout(timeout);

            bool hasThrown = false;
            try
            {
                promise.WaitWithThrow(timeout);
            }
            catch (Exception exc)
            {
                hasThrown = true;
                Assert.IsTrue(exc.GetBaseException().GetType().Equals(typeof(TimeoutException)), exc.ToString());
            }
            watch.Stop();

            Assert.IsTrue(hasThrown);
            Assert.IsTrue(watch.Elapsed >= timeout - delta, watch.Elapsed.ToString());
            Assert.IsTrue(watch.Elapsed <= timeout + delta, watch.Elapsed.ToString());
            Assert.IsTrue(watch.Elapsed < sleepTime, watch.Elapsed.ToString());
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public async Task Async_Task_WithTimeout_Await()
        {
            TimeSpan timeout = TimeSpan.FromMilliseconds(2000);
            TimeSpan sleepTime = TimeSpan.FromMilliseconds(4000);
            TimeSpan delta = TimeSpan.FromMilliseconds(200);
            Stopwatch watch = new Stopwatch();
            watch.Start();

            Task<int> promise = Task<int>.Factory.StartNew(() =>
            {
                Thread.Sleep(sleepTime);
                return 5;
            }).WithTimeout(timeout);

            bool hasThrown = false;
            try
            {
                await promise;
            }
            catch (Exception exc)
            {
                hasThrown = true;
                Assert.IsTrue(exc.GetBaseException().GetType().Equals(typeof(TimeoutException)), exc.ToString());
            }
            watch.Stop();

            Assert.IsTrue(hasThrown);
            Assert.IsTrue(watch.Elapsed >= timeout - delta, watch.Elapsed.ToString());
            Assert.IsTrue(watch.Elapsed <= timeout + delta, watch.Elapsed.ToString());
            Assert.IsTrue(watch.Elapsed < sleepTime, watch.Elapsed.ToString());
        }
    }
}

#pragma warning restore 618

