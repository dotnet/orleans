using System.Diagnostics;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace DefaultCluster.Tests.TimerTests
{
    public class TimerOrleansTest : HostedTestClusterEnsureDefaultStarted
    {
        private readonly ITestOutputHelper output;

        public TimerOrleansTest(ITestOutputHelper output, DefaultClusterFixture fixture)
            : base(fixture)
        {
            this.output = output;
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Basic()
        {
            for (int i = 0; i < 10; i++)
            {
                var grain = GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
                var period = await grain.GetTimerPeriod();
                var timeout = period.Multiply(50);
                var stopwatch = Stopwatch.StartNew();
                var last = 0;
                while (stopwatch.Elapsed < timeout && last < 10)
                {
                    await Task.Delay(period.Divide(2));
                    last = await grain.GetCounter();
                }

                output.WriteLine("value = " + last);
                Assert.True(last >= 10 & last <= 12, last.ToString());

                await grain.StopDefaultTimer();
                await Task.Delay(period.Multiply(2));
                var curr = await grain.GetCounter();
                Assert.True(curr == last || curr == last + 1, "curr == last || curr == last + 1");
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Parallel()
        {
            TimeSpan period = TimeSpan.Zero;
            List<ITimerGrain> grains = new List<ITimerGrain>();
            for (int i = 0; i < 10; i++)
            {
                ITimerGrain grain = GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
                grains.Add(grain);
                period = await grain.GetTimerPeriod(); // activate grains
            }

            var tasks = new List<Task>(grains.Count);
            for (int i = 0; i < grains.Count; i++)
            {
                ITimerGrain grain = grains[i];
                tasks.Add(
                    Task.Run(
                        async () =>
                        {
                            int last = await grain.GetCounter();
                            var stopwatch = Stopwatch.StartNew();
                            var timeout = period.Multiply(50);
                            while (stopwatch.Elapsed < timeout && last < 10)
                            {
                                await Task.Delay(period.Divide(2));
                                last = await grain.GetCounter();
                            }

                            output.WriteLine("value = " + last);
                            Assert.True(last >= 10 && last <= 12, "last >= 10 && last <= 12");
                        }));
            }

            await Task.WhenAll(tasks);
            for (int i = 0; i < grains.Count; i++)
            {
                ITimerGrain grain = grains[i];
                await grain.StopDefaultTimer();
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Migration()
        {
            ITimerGrain grain = GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
            TimeSpan period = await grain.GetTimerPeriod();

            // Ensure that the grain works as it should.
            var last = await grain.GetCounter();
            var stopwatch = Stopwatch.StartNew();
            var timeout = period.Multiply(50);
            while (stopwatch.Elapsed < timeout && last < 10)
            {
                await Task.Delay(period.Divide(2));
                last = await grain.GetCounter();
            }

            last = await grain.GetCounter();
            output.WriteLine("value = " + last);

            // Restart the grain.
            await grain.Deactivate();
            stopwatch.Restart();
            last = await grain.GetCounter();
            Assert.True(last == 0, "Restarted grains should have zero ticks. Actual: " + last);
            period = await grain.GetTimerPeriod();

            // Poke the grain and ensure it still works as it should.
            while (stopwatch.Elapsed < timeout && last < 10)
            {
                await Task.Delay(period.Divide(2));
                last = await grain.GetCounter();
            }

            last = await grain.GetCounter();
            stopwatch.Stop();

            int maximalNumTicks = (int)Math.Round(stopwatch.Elapsed.Divide(period), MidpointRounding.ToPositiveInfinity);
            Assert.True(
                last <= maximalNumTicks,
                $"Assert: last <= maximalNumTicks. Actual: last = {last}, maximalNumTicks = {maximalNumTicks}");

            output.WriteLine(
                "Total Elapsed time = " + (stopwatch.Elapsed.TotalSeconds) + " sec. Expected Ticks = " + maximalNumTicks +
                ". Actual ticks = " + last);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task AsyncTimerTest_GrainCall()
        {
            const string testName = "AsyncTimerTest_GrainCall";
            TimeSpan delay = TimeSpan.FromSeconds(5);
            TimeSpan wait = delay.Multiply(2);

            ITimerCallGrain grain = null;

            Exception error = null;
            try
            {
                grain = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());

                await grain.StartTimer(testName, delay);

                await Task.Delay(wait);

                int tickCount = await grain.GetTickCount();
                Assert.Equal(1, tickCount);

                Exception err = await grain.GetException();
                Assert.Null(err); // Should be no exceptions during timer callback
            }
            catch (Exception exc)
            {
                output.WriteLine(exc);
                error = exc;
            }

            try
            {
                if (grain != null) await grain.StopTimer(testName);
            }
            catch (Exception exc)
            {
                // Ignore
                output.WriteLine("Ignoring exception from StopTimer : {0}", exc);
            }

            if (error != null)
            {
                Assert.Fail($"Test {testName} failed with error {error}");
            }
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task GrainTimer_Change()
        {
            const string testName = nameof(GrainTimer_Change);
            TimeSpan delay = TimeSpan.FromSeconds(5);
            TimeSpan wait = delay.Multiply(2);

            var grain = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());

            await grain.StartTimer(testName, delay);

            await Task.Delay(wait);

            int tickCount = await grain.GetTickCount();
            Assert.Equal(1, tickCount);

            await grain.RestartTimer(testName, delay);

            await Task.Delay(wait);

            tickCount = await grain.GetTickCount();
            Assert.Equal(2, tickCount);

            // Infinite timeouts should be valid.
            await grain.RestartTimer(testName, Timeout.InfiniteTimeSpan);
            await grain.RestartTimer(testName, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // Zero and sub-ms timeouts should be valid (rounded up to 1ms)
            await grain.RestartTimer(testName, TimeSpan.Zero);
            await grain.RestartTimer(testName, TimeSpan.FromMicroseconds(10));
            await grain.RestartTimer(testName, TimeSpan.Zero, TimeSpan.Zero);
            await grain.RestartTimer(testName, TimeSpan.FromMicroseconds(10), TimeSpan.FromMicroseconds(10));
            await grain.RestartTimer(testName, TimeSpan.FromMilliseconds(-0.4));
            await grain.RestartTimer(testName, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(-0.5));

            // Invalid values
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.FromSeconds(-5)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.MaxValue));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-5)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.FromSeconds(1), TimeSpan.MaxValue));

            Exception err = await grain.GetException();
            Assert.Null(err); // Should be no exceptions during timer callback

            // Valid operations called from within a timer: updating the period and disposing the timer.
            var grain2 = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());
            await grain2.StartTimer(testName, delay, "update_period");
            await Task.Delay(wait);
            Assert.Null(await grain2.GetException()); // Should be no exceptions during timer callback
            Assert.Equal(1, await grain2.GetTickCount());

            var grain3 = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());
            await grain3.StartTimer(testName, delay, "dispose_timer");
            await Task.Delay(wait);
            Assert.Null(await grain3.GetException()); // Should be no exceptions during timer callback
            Assert.Equal(1, await grain3.GetTickCount());

            await grain.StopTimer(testName);
        }
    }
}
