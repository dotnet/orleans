using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Xunit;

#pragma warning disable 618

namespace UnitTests
{
    public class Async_TimingTests
    {
        private readonly Logger logger;

        public Async_TimingTests()
        {
            LogManager.Initialize(ClientConfiguration.StandardLoad());
            logger = LogManager.GetLogger("AC_TimingTests", LoggerType.Application);
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
                Assert.True(exc.GetBaseException().GetType().Equals(typeof(TimeoutException)), exc.ToString());
            }
            watch.Stop();

            Assert.True(hasThrown);
            Assert.True(watch.Elapsed >= timeout - delta, watch.Elapsed.ToString());
            Assert.True(watch.Elapsed <= timeout + delta, watch.Elapsed.ToString());
            Assert.True(watch.Elapsed < sleepTime, watch.Elapsed.ToString());
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public async Task Async_Task_WithTimeout_Await()
        {
            TimeSpan timeout = TimeSpan.FromMilliseconds(2000);
            TimeSpan sleepTime = TimeSpan.FromMilliseconds(4000);
            TimeSpan delta = TimeSpan.FromMilliseconds(300);
            Stopwatch watch = Stopwatch.StartNew();

            Task<int> promise = Task<int>.Factory.StartNew(() =>
            {
                Thread.Sleep(sleepTime);
                return 5;
            }).WithTimeout(timeout);

            await Assert.ThrowsAsync<TimeoutException>(() => promise);
            watch.Stop();

            Assert.True(watch.Elapsed >= timeout - delta, watch.Elapsed.ToString());
            Assert.True(watch.Elapsed <= timeout + delta, watch.Elapsed.ToString());
            Assert.True(watch.Elapsed < sleepTime, watch.Elapsed.ToString());
        }
    }
}

#pragma warning restore 618

