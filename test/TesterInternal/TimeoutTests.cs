using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests
{
    // if we paralellize tests, this should run in isolation
    public class TimeoutTests : HostedTestClusterEnsureDefaultStarted, IDisposable
    {
        private readonly ITestOutputHelper output;
        private TimeSpan originalTimeout;
        
        public TimeoutTests(ITestOutputHelper output)
        {
            this.output = output;
            originalTimeout = RuntimeClient.Current.GetResponseTimeout();
        }

        public void Dispose()
        {
            RuntimeClient.Current.SetResponseTimeout(originalTimeout);
        }

        [Fact, TestCategory("Functional"), TestCategory("Timeout")]
        public void Timeout_LongMethod()
        {
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
                Assert.True(false, "Should have thrown");
            }
            catch (Exception exc)
            {
                stopwatch.Stop();
                Exception baseExc = exc.GetBaseException();
                if (!(baseExc is TimeoutException))
                {
                    Assert.True(false, "Should not have got here " + exc);
                }
            }
            output.WriteLine("Waited for " + stopwatch.Elapsed);
            Assert.True(!finished);
            Assert.True(stopwatch.Elapsed >= timeout.Multiply(0.9), "Waited less than " + timeout.Multiply(0.9) + ". Waited " + stopwatch.Elapsed);
            Assert.True(stopwatch.Elapsed <= timeout.Multiply(2), "Waited longer than " + timeout.Multiply(2) + ". Waited " + stopwatch.Elapsed);
            Assert.True(promise.Status == TaskStatus.Faulted);

            // try to re-use the promise and should fail immideately.
            try
            {
                stopwatch = new Stopwatch();
                promise.Wait();
                Assert.True(false, "Should have thrown");
            }
            catch (Exception exc)
            {
                stopwatch.Stop();
                Exception baseExc = exc.GetBaseException();
                if (!(baseExc is TimeoutException))
                {
                    Assert.True(false, "Should not have got here " + exc);
                }
            }
            Assert.True(stopwatch.Elapsed <= timeout.Multiply(0.1), "Waited longer than " + timeout.Multiply(0.1) + ". Waited " + stopwatch.Elapsed);
            Assert.True(promise.Status == TaskStatus.Faulted);
        }
    }
}
