using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

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
            // Save original timeout to restore it after tests
            originalTimeout = this.runtimeClient.GetResponseTimeout();
            this.typeResolver = this.HostedCluster.ServiceProvider.GetRequiredService<GrainInterfaceTypeResolver>();
        }

        public virtual void Dispose()
        {
            // Restore original timeout to avoid affecting other tests
            this.runtimeClient.SetResponseTimeout(originalTimeout);
        }

        /// <summary>
        /// Tests that grain calls timeout correctly when the method takes longer than the response timeout.
        /// Verifies:
        /// - TimeoutException is thrown after the configured timeout period
        /// - The timeout occurs within expected bounds (not too early, not too late)
        /// - Request tracking is cleaned up (no lingering requests)
        /// - Re-awaiting the same task fails immediately
        /// </summary>
        [Fact, TestCategory("Functional"), TestCategory("Timeout")]
        public async Task Timeout_LongMethod()
        {
            bool finished = false;
            var grainName = typeof (ErrorGrain).FullName;
            IErrorGrain grain = this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainName);
            var errorGrainType = this.typeResolver.GetGrainInterfaceType(typeof(IErrorGrain));
            // Set a 1-second timeout for this test
            TimeSpan timeout = TimeSpan.FromMilliseconds(1000);
            this.runtimeClient.SetResponseTimeout(timeout);

            // Call a method that takes 4x longer than the timeout
            Task promise = grain.LongMethod((int)timeout.Multiply(4).TotalMilliseconds);
            //promise = grain.LongMethodWithError(2000);

            // Note: There's a potential race condition in debugger where the call might complete
            // Measure how long we wait for the timeout
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            try
            {
                await promise.WaitAsync(timeout.Multiply(3));
                finished = true;
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
            output.WriteLine("Waited for " + stopwatch.Elapsed);
            Assert.True(!finished);
            // Verify timeout occurred within expected bounds (90% to 350% of configured timeout)
            Assert.True(stopwatch.Elapsed >= timeout.Multiply(0.9), "Waited less than " + timeout.Multiply(0.9) + ". Waited " + stopwatch.Elapsed);
            Assert.True(stopwatch.Elapsed <= timeout.Multiply(3.5), "Waited longer than " + timeout.Multiply(3.5) + ". Waited " + stopwatch.Elapsed);
            Assert.True(promise.Status == TaskStatus.Faulted);

            // Verify request tracking is cleaned up - no requests should be pending
            Assert.Equal(expected: 0, actual: this.runtimeClient.GetRunningRequestsCount(errorGrainType));

            // Re-awaiting a timed-out task should fail immediately
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
        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/3995"), TestCategory("SlowBVT")]
        public async Task CallThatShouldHaveBeenDroppedNotExecutedTest()
        {
            var responseTimeout = TimeSpan.FromSeconds(2);
            this.runtimeClient.SetResponseTimeout(responseTimeout);

            var target = Client.GetGrain<ILongRunningTaskGrain<int>>(Guid.NewGuid());

            // First call: Takes 5 seconds but client times out after 2 seconds
            var delay = TimeSpan.FromSeconds(5);
            var firstCall = target.LongRunningTask(1, responseTimeout + delay);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            // Second call: Should be dropped because grain is still busy with first call
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

            // Wait for first call to complete on the silo
            await Task.Delay(delay);

            // Verify only the first call executed (value = 1), second call was dropped
            Assert.Equal(1, await target.GetLastValue());
        }
    }
}
