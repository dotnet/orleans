using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests
{
    /// <summary>
    /// Tests for Orleans timeout mechanisms and request cancellation.
    /// 
    /// Orleans implements timeouts to prevent indefinite waiting on grain calls:
    /// - Each grain call has a configurable response timeout
    /// - If a grain method doesn't complete within the timeout, a TimeoutException is thrown
    /// - The original request continues executing on the silo (not cancelled)
    /// - Subsequent calls to a busy grain may be dropped to prevent queue buildup
    /// 
    /// These tests verify:
    /// - Timeout exceptions are thrown at the appropriate time
    /// - Request tracking is properly cleaned up after timeouts
    /// - Call dropping behavior for overloaded grains
    /// 
    /// Note: These tests modify global timeout settings, so they should run in isolation.
    /// </summary>
    [TestArea("Runtime")]
    public class TimeoutTests : HostedTestClusterEnsureDefaultStarted, IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly TimeSpan originalTimeout;
        private readonly IRuntimeClient runtimeClient;
        private readonly GrainInterfaceTypeResolver typeResolver;

        public TimeoutTests(ITestOutputHelper output, DefaultClusterFixture fixture) : base(fixture)
        {
            this.output = output;
            this.runtimeClient = this.HostedCluster.ServiceProvider.GetRequiredService<IRuntimeClient>();
            originalTimeout = this.runtimeClient.GetResponseTimeout();
            this.typeResolver = this.HostedCluster.ServiceProvider.GetRequiredService<GrainInterfaceTypeResolver>();
        }

        public virtual void Dispose()
        {
            this.runtimeClient.SetResponseTimeout(originalTimeout);
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Fact, TestCategory("Functional"), TestCategory("Timeout")]
        public async Task Timeout_LongMethod()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            bool finished = false;
            var grainName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainName);
            var errorGrainType = this.typeResolver.GetGrainInterfaceType(typeof(IErrorGrain));
            TimeSpan timeout = TimeSpan.FromMilliseconds(1000);
            this.runtimeClient.SetResponseTimeout(timeout);

            Task promise = grain.LongMethod((int)timeout.Multiply(4).TotalMilliseconds);
            //promise = grain.LongMethodWithError(2000);

            // there is a race in the test here. If run in debugger, the invocation can actually finish OK
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            try
            {
                await promise.WaitAsync(timeout.Multiply(3), cancellationToken);
                finished = true;
                Assert.Fail("Should have thrown");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
            output.WriteLine("Waited for " + stopwatch.Elapsed);
            Assert.True(!finished);
            Assert.True(stopwatch.Elapsed >= timeout.Multiply(0.9), "Waited less than " + timeout.Multiply(0.9) + ". Waited " + stopwatch.Elapsed);
            Assert.True(stopwatch.Elapsed <= timeout.Multiply(3.5), "Waited longer than " + timeout.Multiply(3.5) + ". Waited " + stopwatch.Elapsed);
            Assert.True(promise.Status == TaskStatus.Faulted);

            Assert.Equal(expected: 0, actual: this.runtimeClient.GetRunningRequestsCount(errorGrainType));

            // try to re-use the promise and should fail immediately.
            try
            {
                stopwatch = new Stopwatch();
                await promise;
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
            Assert.True(stopwatch.Elapsed <= timeout.Multiply(0.1), "Waited longer than " + timeout.Multiply(0.1) + ". Waited " + stopwatch.Elapsed);
            Assert.True(promise.Status == TaskStatus.Faulted);
        }

        /// <summary>
        /// Tests call dropping behavior when a grain is overloaded.
        /// When a grain is busy processing a long-running request and the client times out,
        /// subsequent calls to the same activation may be dropped to prevent queue buildup.
        /// 
        /// Scenario:
        /// 1. First call takes longer than timeout - client gets TimeoutException
        /// 2. Second call arrives while first is still running - should be dropped
        /// 3. Verify only the first call actually executed on the grain
        /// 
        /// Currently skipped due to issue #3995.
        /// </summary>
        [TestSuite("SlowBVT")]
        [TestProvider("None")]
        [Fact(Skip = "https://github.com/dotnet/orleans/issues/3995"), TestCategory("SlowBVT")]
        public async Task CallThatShouldHaveBeenDroppedNotExecutedTest()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var responseTimeout = TimeSpan.FromSeconds(2);
            this.runtimeClient.SetResponseTimeout(responseTimeout);

            var target = Client.GetGrain<ILongRunningTaskGrain<int>>(Guid.NewGuid());

            // First call should be successful, but client will not receive the response
            var delay = TimeSpan.FromSeconds(5);
            var firstCall = target.LongRunningTask(1, responseTimeout + delay);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            // Second call: Should be dropped because grain is still busy with first call
            var secondCall = target.LongRunningTask(2, TimeSpan.Zero);

            try
            {
                await Assert.ThrowsAsync<TimeoutException>(() => firstCall);
                await Assert.ThrowsAsync<TimeoutException>(() => secondCall);
            }
            catch
            {
                output.WriteLine($"firstCall: {firstCall.Status}, Exception: {firstCall.Exception}");
                output.WriteLine($"secondCall: {secondCall.Status}, Exception: {secondCall.Exception}");
                throw;
            }

            // Wait for first call to complete on the silo
            await Task.Delay(delay, cancellationToken);

            Assert.Equal(1, await target.GetLastValue());
        }
    }
}
