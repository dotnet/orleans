using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests
{
    // if we parallelize tests, this should run in isolation 
    public class TimeoutTests : HostedTestClusterEnsureDefaultStarted, IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly TimeSpan originalTimeout;
        private readonly IRuntimeClient runtimeClient;

        public TimeoutTests(ITestOutputHelper output, DefaultClusterFixture fixture) : base(fixture)
        {
            this.output = output;
            this.runtimeClient = this.HostedCluster.ServiceProvider.GetRequiredService<IRuntimeClient>();
            originalTimeout = this.runtimeClient.GetResponseTimeout();
        }

        public virtual void Dispose()
        {
            this.runtimeClient.SetResponseTimeout(originalTimeout);
        }

        [Fact, TestCategory("Functional"), TestCategory("Timeout")]
        public void Timeout_LongMethod()
        {
            bool finished = false;
            var grainName = typeof (ErrorGrain).FullName;
            IErrorGrain grain = this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainName);
            TimeSpan timeout = TimeSpan.FromMilliseconds(1000);
            this.runtimeClient.SetResponseTimeout(timeout);

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
            Assert.True(stopwatch.Elapsed <= timeout.Multiply(3.5), "Waited longer than " + timeout.Multiply(3.5) + ". Waited " + stopwatch.Elapsed);
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

        [SkippableFact(Skip= "https://github.com/dotnet/orleans/issues/3995"), TestCategory("SlowBVT")]
        public async Task CallThatShouldHaveBeenDroppedNotExecutedTest()
        {
            var responseTimeout = TimeSpan.FromSeconds(2);
            this.runtimeClient.SetResponseTimeout(responseTimeout);

            var target = Client.GetGrain<ILongRunningTaskGrain<int>>(Guid.NewGuid());

            // First call should be successful, but client will not receive the response
            var delay = TimeSpan.FromSeconds(5);
            var firstCall = target.LongRunningTask(1, responseTimeout + delay);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            // Second call should be dropped by the silo
            var secondCall = target.LongRunningTask(2, TimeSpan.Zero);

            try
            {
                await Assert.ThrowsAsync<TimeoutException>(() => firstCall);
                await Assert.ThrowsAsync<TimeoutException>(() => secondCall);
            }
            catch
            {
                output.WriteLine(firstCall.IsFaulted ? $"firstCall: faulted" : $"firstCall: {firstCall.Result}");
                output.WriteLine(secondCall.IsFaulted ? $"secondCall: faulted" : $"secondCall: {secondCall.Result}");
                throw;
            }

            await Task.Delay(delay);

            Assert.Equal(1, await target.GetLastValue());
        }
    }
}
