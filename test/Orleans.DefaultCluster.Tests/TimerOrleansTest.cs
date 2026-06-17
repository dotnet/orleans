using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTestGrains;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace DefaultCluster.Tests.TimerTests
{
    public static class TimerOrleansTestCollection
    {
        public const string Name = nameof(TimerOrleansTest);
    }

    [CollectionDefinition(TimerOrleansTestCollection.Name)]
    public sealed class TimerOrleansTestCollectionDefinition : ICollectionFixture<TimerOrleansTest.Fixture>
    {
    }

    /// <summary>
    /// Tests for Orleans Timer functionality.
    /// Timers provide grain-local, non-durable periodic callbacks. Unlike reminders,
    /// timers are active only while a grain is activated and don't persist across
    /// deactivations. They're ideal for short-lived periodic tasks, polling,
    /// timeouts, and other scenarios where persistence isn't required.
    /// Timers are more efficient than reminders for high-frequency operations.
    /// </summary>
    [Collection(TimerOrleansTestCollection.Name)]
    public class TimerOrleansTest : OrleansTestingBase
    {
        private const int ExpectedDefaultTimerTicks = 10;
        private static readonly TimeSpan OneShotTimerDelay = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan TimerCallbackDelay = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan TimerOverloadDueTime = TimeSpan.FromMilliseconds(25);
        private static readonly TimeSpan TimerDiagnosticTimeout = TimeSpan.FromSeconds(10);
        private readonly Fixture fixture;
        private readonly ITestOutputHelper output;

        public TimerOrleansTest(ITestOutputHelper output, Fixture fixture)
        {
            this.fixture = fixture;
            this.output = output;
        }

        private IGrainFactory GrainFactory => fixture.GrainFactory;

        private async Task<int> AdvanceDefaultTimerTicksAsync(ITimerGrain grain, TimeSpan period)
        {
            using var timerObserver = TimerDiagnosticObserver.Create();
            for (var expectedTickCount = 1; expectedTickCount <= ExpectedDefaultTimerTicks; expectedTickCount++)
            {
                await AdvanceTimerToTickCountAsync(grain, timerObserver, period, expectedTickCount);
                await grain.GetCounter();
            }

            return await grain.GetCounter();
        }

        private async Task AdvanceDefaultTimerTicksAsync<TGrain>(IReadOnlyList<TGrain> grains, TimeSpan period)
            where TGrain : ITimerGrain
        {
            using var timerObserver = TimerDiagnosticObserver.Create();
            for (var expectedTickCount = 1; expectedTickCount <= ExpectedDefaultTimerTicks; expectedTickCount++)
            {
                var waitForTicks = Task.WhenAll(grains.Select(g => timerObserver.WaitForTickCountAsync(g, expectedTickCount, TimerDiagnosticTimeout)));
                fixture.AdvanceTime(period);
                await waitForTicks;
                await Task.WhenAll(grains.Select(g => g.GetCounter()));
            }
        }

        private async Task AdvanceTimerToTickCountAsync(IAddressable grain, TimerDiagnosticObserver timerObserver, TimeSpan dueTime, int expectedTickCount)
        {
            var waitForTick = timerObserver.WaitForTickCountAsync(grain, expectedTickCount, TimerDiagnosticTimeout);
            fixture.AdvanceTime(dueTime);
            await waitForTick;
        }

        private async Task AdvanceTimerToTickCountAsync(
            IAddressable grain,
            TimerDiagnosticObserver timerObserver,
            TimeSpan dueTime,
            TimeSpan callbackDelay,
            int expectedTickCount)
        {
            using var callbackObserver = TimerCallbackDiagnosticObserver.Create();
            var grainId = grain.GetGrainId();
            var delayScheduledCount = callbackObserver.GetDelayScheduledCount(grainId);

            fixture.AdvanceTime(dueTime);
            for (var tickCount = timerObserver.GetTickCount(grain.GetGrainId()) + 1; tickCount <= expectedTickCount; tickCount++)
            {
                await timerObserver.WaitForTickStartCountAsync(grain, tickCount, TimerDiagnosticTimeout);
                await callbackObserver.WaitForDelayScheduledCountAsync(grainId, ++delayScheduledCount, TimerDiagnosticTimeout);

                var waitForTickStop = timerObserver.WaitForTickCountAsync(grain, tickCount, TimerDiagnosticTimeout);
                fixture.AdvanceTime(callbackDelay);
                await waitForTickStop;
            }
        }

        private async Task<int> DriveExternalTickUntilTimerTicks(INonReentrantTimerCallGrain grain, TimerDiagnosticObserver timerObserver, TimeSpan dueTime, int expectedTimerTicks)
        {
            using var callbackObserver = TimerCallbackDiagnosticObserver.Create();
            var grainId = grain.GetGrainId();
            var externalTick = grain.ExternalTick("external");
            await callbackObserver.WaitForDelayScheduledCountAsync(grainId, callbackObserver.GetDelayScheduledCount(grainId) + 1, TimerDiagnosticTimeout);
            fixture.AdvanceTime(TimerCallbackDelay);
            await externalTick;
            await AdvanceTimerToTickCountAsync(grain, timerObserver, dueTime, TimerCallbackDelay, expectedTimerTicks);
            return 1;
        }

        private async Task RunSelfDisposingTimerAsync(ITimerCallGrain grain)
        {
            using var timerObserver = TimerDiagnosticObserver.Create();
            var runTimer = grain.RunSelfDisposingTimer();
            var created = await timerObserver.WaitForTimerCreatedAsync(grain, TimerDiagnosticTimeout);
            await AdvanceTimerToTickCountAsync(grain, timerObserver, created.DueTime, TimeSpan.FromMilliseconds(100), expectedTickCount: 1);
            await runTimer;
        }

        private sealed class TimerCallbackDiagnosticObserver : IDisposable, IObserver<TimerGrainCallbackEvents.CallbackEvent>
        {
            private readonly ConcurrentBag<TimerGrainCallbackEvents.DelayScheduled> delayScheduledEvents = new();
            private readonly object changeLock = new();
            private TaskCompletionSource changed = CreateCompletionSource();
            private IDisposable subscription;

            public static TimerCallbackDiagnosticObserver Create()
            {
                var observer = new TimerCallbackDiagnosticObserver();
                observer.subscription = TimerGrainCallbackEvents.AllEvents.Subscribe(observer);
                return observer;
            }

            public int GetDelayScheduledCount(GrainId grainId)
            {
                return delayScheduledEvents.Count(e => e.GrainId == grainId);
            }

            public async Task WaitForDelayScheduledCountAsync(GrainId grainId, int expectedCount, TimeSpan timeout)
            {
                using var cts = new CancellationTokenSource(timeout);

                while (true)
                {
                    Task changedTask;
                    lock (changeLock)
                    {
                        var currentCount = GetDelayScheduledCount(grainId);
                        if (currentCount >= expectedCount)
                        {
                            return;
                        }

                        changedTask = changed.Task;
                    }

                    try
                    {
                        await changedTask.WaitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                var finalCount = GetDelayScheduledCount(grainId);
                if (finalCount >= expectedCount)
                {
                    return;
                }

                throw new TimeoutException($"Timed out waiting for {expectedCount} timer callbacks to schedule their fake delay on grain {grainId}. Current count: {finalCount} after {timeout}");
            }

            void IObserver<TimerGrainCallbackEvents.CallbackEvent>.OnNext(TimerGrainCallbackEvents.CallbackEvent value)
            {
                if (value is TimerGrainCallbackEvents.DelayScheduled delayScheduled)
                {
                    delayScheduledEvents.Add(delayScheduled);
                    SignalChanged();
                }
            }

            void IObserver<TimerGrainCallbackEvents.CallbackEvent>.OnError(Exception error)
            {
            }

            void IObserver<TimerGrainCallbackEvents.CallbackEvent>.OnCompleted()
            {
            }

            public void Dispose()
            {
                subscription?.Dispose();
            }

            private static TaskCompletionSource CreateCompletionSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

            private void SignalChanged()
            {
                TaskCompletionSource previous;
                lock (changeLock)
                {
                    previous = changed;
                    changed = CreateCompletionSource();
                }

                previous.TrySetResult();
            }
        }

        public sealed class Fixture : BaseInProcessTestClusterFixture
        {
            private readonly FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

            protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
            {
                builder.ConfigureSilo((_, siloBuilder) =>
                {
                    siloBuilder
                        .Configure<SiloMessagingOptions>(o => o.ClientGatewayShutdownNotificationTimeout = default)
                        .UseInMemoryReminderService()
                        .UseInMemoryDurableJobs()
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("MemoryStore");

                    siloBuilder.Services.AddSingleton(timeProvider);
                    siloBuilder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>()));
                });
            }

            public void AdvanceTime(TimeSpan duration) => timeProvider.Advance(duration);
        }

        /// <summary>
        /// Tests basic timer functionality including start and stop operations.
        /// Verifies that timers tick at expected intervals, can be stopped,
        /// and that stopping a timer prevents further ticks. This demonstrates
        /// the fundamental timer lifecycle within a grain activation.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Basic()
        {
            for (int i = 0; i < 10; i++)
            {
                var grain = GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
                var period = await grain.GetTimerPeriod();
                var last = await AdvanceDefaultTimerTicksAsync(grain, period);

                output.WriteLine("value = " + last);
                Assert.Equal(ExpectedDefaultTimerTicks, last);

                await grain.StopDefaultTimer();
                fixture.AdvanceTime(period.Multiply(2));
                var curr = await grain.GetCounter();
                Assert.Equal(last, curr);
            }
        }

        /// <summary>
        /// Tests multiple grains with timers running in parallel.
        /// Verifies that each grain maintains its own independent timer
        /// and that timers across different grain activations don't interfere
        /// with each other, demonstrating timer isolation per grain.
        /// </summary>
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

            await AdvanceDefaultTimerTicksAsync(grains, period);
            for (int i = 0; i < grains.Count; i++)
            {
                ITimerGrain grain = grains[i];
                var last = await grain.GetCounter();
                output.WriteLine("value = " + last);
                Assert.Equal(ExpectedDefaultTimerTicks, last);
                await grain.StopDefaultTimer();
            }
        }

        /// <summary>
        /// Tests timer behavior across grain deactivation and reactivation.
        /// Verifies that timers don't persist across grain lifecycle - when a grain
        /// is deactivated and reactivated, timers start fresh. This demonstrates
        /// the non-durable nature of timers compared to reminders.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Migration()
        {
            ITimerGrain grain = GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
            TimeSpan period = await grain.GetTimerPeriod();

            // Ensure that the grain works as it should.
            var last = await AdvanceDefaultTimerTicksAsync(grain, period);
            Assert.Equal(ExpectedDefaultTimerTicks, last);
            output.WriteLine("value = " + last);

            // Restart the grain.
            await grain.Deactivate();
            last = await grain.GetCounter();
            Assert.True(last == 0, "Restarted grains should have zero ticks. Actual: " + last);
            period = await grain.GetTimerPeriod();

            // Poke the grain and ensure it still works as it should.
            last = await AdvanceDefaultTimerTicksAsync(grain, period);
            Assert.Equal(ExpectedDefaultTimerTicks, last);

            output.WriteLine(
                "Virtual elapsed time = " + (period.Multiply(ExpectedDefaultTimerTicks).TotalSeconds) + " sec. Expected ticks = " + ExpectedDefaultTimerTicks +
                ". Actual ticks = " + last);
        }

        /// <summary>
        /// Tests timers that make asynchronous grain calls in their callbacks.
        /// Verifies that timer callbacks can perform grain-to-grain communication
        /// and that exceptions in timer callbacks are captured and don't crash
        /// the grain. Important for timer-based orchestration patterns.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task AsyncTimerTest_GrainCall()
        {
            const string testName = "AsyncTimerTest_GrainCall";
            TimeSpan delay = OneShotTimerDelay;

            ITimerCallGrain grain = null;

            Exception error = null;
            using var timerObserver = TimerDiagnosticObserver.Create();
            try
            {
                grain = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());

                await grain.StartTimer(testName, delay);

                await AdvanceTimerToTickCountAsync(grain, timerObserver, delay, TimerCallbackDelay, expectedTickCount: 1);

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

        /// <summary>
        /// Tests all timer creation overloads and their behavior.
        /// Verifies that different timer registration methods (with/without state,
        /// different period specifications) work correctly and that all timer
        /// variants execute their callbacks as expected.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task GrainTimer_TestAllOverloads()
        {
            var grain = GrainFactory.GetGrain<ITimerRequestGrain>(GetRandomGrainId());

            using var timerObserver = TimerDiagnosticObserver.Create();
            var numTimers = await grain.TestAllTimerOverloads();
            var waitForTimers = timerObserver.WaitForTickCountAsync(grain, numTimers, TimerDiagnosticTimeout);
            fixture.AdvanceTime(TimerOverloadDueTime);
            await waitForTimers;
            await grain.TestCompletedTimerResults();
        }

        /// <summary>
        /// Tests that timers can safely dispose themselves from their own callbacks.
        /// Verifies that self-disposal doesn't cause deadlocks or exceptions,
        /// which is important for one-shot timer patterns or timers that
        /// need to cancel themselves based on conditions.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task GrainTimer_DisposeFromCallback()
        {
            // Schedule a timer which disposes itself from its own callback.
            var grain = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());
            await RunSelfDisposingTimerAsync(grain);

            var pocoGrain = GrainFactory.GetGrain<IPocoTimerCallGrain>(GetRandomGrainId());
            await RunSelfDisposingTimerAsync(pocoGrain);
        }

        /// <summary>
        /// Tests timer behavior in non-reentrant grains.
        /// Verifies that multiple timers in a non-reentrant grain respect
        /// the grain's concurrency constraints - timer callbacks don't overlap
        /// and are serialized with other grain methods, maintaining the
        /// single-threaded execution model.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task NonReentrantGrainTimer_Test()
        {
            const string testName = "NonReentrantGrainTimer_Test";
            var delay = OneShotTimerDelay;

            using var timerObserver = TimerDiagnosticObserver.Create();
            var grain = GrainFactory.GetGrain<INonReentrantTimerCallGrain>(GetRandomGrainId());

            // Schedule multiple timers with the same delay
            await grain.StartTimer(testName, delay);
            await grain.StartTimer($"{testName}_1", delay);
            await grain.StartTimer($"{testName}_2", delay);

            // Invoke some non-interleaving methods while waiting for the timer callbacks.
            var externalTicks = await DriveExternalTickUntilTimerTicks(grain, timerObserver, delay, expectedTimerTicks: 3);

            var tickCount = await grain.GetTickCount();

            Assert.Equal(3 + externalTicks, tickCount);

            var err = await grain.GetException();
            Assert.Null(err); // Should be no exceptions during timer callback

            await grain.StopTimer(testName);
            await grain.StopTimer($"{testName}_1");
            await grain.StopTimer($"{testName}_2");
        }

        /// <summary>
        /// Tests changing timer periods and due times after creation.
        /// Verifies that timer.Change() correctly updates timing parameters,
        /// handles edge cases (infinite, zero, negative values), and that
        /// timers can be safely modified from within their own callbacks.
        /// Essential for adaptive timing scenarios.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task GrainTimer_Change()
        {
            const string testName = nameof(GrainTimer_Change);
            TimeSpan delay = OneShotTimerDelay;

            using var timerObserver = TimerDiagnosticObserver.Create();
            var grain = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());

            await grain.StartTimer(testName, delay);

            await AdvanceTimerToTickCountAsync(grain, timerObserver, delay, TimerCallbackDelay, expectedTickCount: 1);

            int tickCount = await grain.GetTickCount();
            Assert.Equal(1, tickCount);

            await grain.RestartTimer(testName, delay);

            await AdvanceTimerToTickCountAsync(grain, timerObserver, delay, TimerCallbackDelay, expectedTickCount: 2);

            tickCount = await grain.GetTickCount();
            Assert.Equal(2, tickCount);

            await grain.TestTimerChangeArguments();

            Exception err = await grain.GetException();
            Assert.Null(err); // Should be no exceptions during timer callback

            // Valid operations called from within a timer: updating the period and disposing the timer.
            var grain2 = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());
            await grain2.StartTimer(testName, delay, "update_period");
            await AdvanceTimerToTickCountAsync(grain2, timerObserver, delay, TimerCallbackDelay, expectedTickCount: 1);
            Assert.Null(await grain2.GetException()); // Should be no exceptions during timer callback
            Assert.Equal(1, await grain2.GetTickCount());

            var grain3 = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());
            await grain3.StartTimer(testName, delay, "dispose_timer");
            await AdvanceTimerToTickCountAsync(grain3, timerObserver, delay, TimerCallbackDelay, expectedTickCount: 1);
            var grain3TickCount = await grain3.GetTickCount();

            Assert.Null(await grain3.GetException()); // Should be no exceptions during timer callback
            Assert.Equal(1, grain3TickCount);

            await grain.StopTimer(testName);
        }

        /// <summary>
        /// Tests basic timer functionality with POCO (Plain Old CLR Object) grains.
        /// Verifies that timers work identically with POCO grains as with
        /// traditional Grain-derived classes, demonstrating that the timer
        /// infrastructure is independent of the grain implementation style.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Basic_Poco()
        {
            for (int i = 0; i < 10; i++)
            {
                var grain = GrainFactory.GetGrain<IPocoTimerGrain>(GetRandomGrainId());
                var period = await grain.GetTimerPeriod();
                var last = await AdvanceDefaultTimerTicksAsync(grain, period);

                output.WriteLine("value = " + last);
                Assert.Equal(ExpectedDefaultTimerTicks, last);

                await grain.StopDefaultTimer();
                fixture.AdvanceTime(period.Multiply(2));
                var curr = await grain.GetCounter();
                Assert.Equal(last, curr);
            }
        }

        /// <summary>
        /// Tests parallel timer execution across multiple POCO grain instances.
        /// Verifies that POCO grains maintain timer isolation just like
        /// traditional grains, with each instance having independent timers
        /// that don't interfere with each other.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Parallel_Poco()
        {
            TimeSpan period = TimeSpan.Zero;
            List<IPocoTimerGrain> grains = new List<IPocoTimerGrain>();
            for (int i = 0; i < 10; i++)
            {
                IPocoTimerGrain grain = GrainFactory.GetGrain<IPocoTimerGrain>(GetRandomGrainId());
                grains.Add(grain);
                period = await grain.GetTimerPeriod(); // activate grains
            }

            await AdvanceDefaultTimerTicksAsync(grains, period);
            for (int i = 0; i < grains.Count; i++)
            {
                IPocoTimerGrain grain = grains[i];
                var last = await grain.GetCounter();
                output.WriteLine("value = " + last);
                Assert.Equal(ExpectedDefaultTimerTicks, last);
                await grain.StopDefaultTimer();
            }
        }

        /// <summary>
        /// Tests timer behavior across POCO grain deactivation/reactivation.
        /// Verifies that POCO grains exhibit the same non-persistent timer
        /// behavior as traditional grains - timers are lost on deactivation
        /// and start fresh on reactivation.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Migration_Poco()
        {
            IPocoTimerGrain grain = GrainFactory.GetGrain<IPocoTimerGrain>(GetRandomGrainId());
            TimeSpan period = await grain.GetTimerPeriod();

            // Ensure that the grain works as it should.
            var last = await AdvanceDefaultTimerTicksAsync(grain, period);
            Assert.Equal(ExpectedDefaultTimerTicks, last);
            output.WriteLine("value = " + last);

            // Restart the grain.
            await grain.Deactivate();
            last = await grain.GetCounter();
            Assert.True(last == 0, "Restarted grains should have zero ticks. Actual: " + last);
            period = await grain.GetTimerPeriod();

            // Poke the grain and ensure it still works as it should.
            last = await AdvanceDefaultTimerTicksAsync(grain, period);
            Assert.Equal(ExpectedDefaultTimerTicks, last);

            output.WriteLine(
                "Virtual elapsed time = " + (period.Multiply(ExpectedDefaultTimerTicks).TotalSeconds) + " sec. Expected ticks = " + ExpectedDefaultTimerTicks +
                ". Actual ticks = " + last);
        }

        /// <summary>
        /// Tests asynchronous grain calls from POCO grain timer callbacks.
        /// Verifies that POCO grain timers can perform async operations
        /// including grain-to-grain calls, with proper exception handling
        /// in the timer callback context.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task AsyncTimerTest_GrainCall_Poco()
        {
            const string testName = "AsyncTimerTest_GrainCall";
            TimeSpan delay = OneShotTimerDelay;

            IPocoTimerCallGrain grain = null;

            Exception error = null;
            using var timerObserver = TimerDiagnosticObserver.Create();
            try
            {
                grain = GrainFactory.GetGrain<IPocoTimerCallGrain>(GetRandomGrainId());

                await grain.StartTimer(testName, delay);

                await AdvanceTimerToTickCountAsync(grain, timerObserver, delay, TimerCallbackDelay, expectedTickCount: 1);

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

        /// <summary>
        /// Tests all timer registration overloads with POCO grains.
        /// Ensures that POCO grains support the full range of timer
        /// registration methods, maintaining API compatibility with
        /// traditional grain implementations.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task GrainTimer_TestAllOverloads_Poco()
        {
            var grain = GrainFactory.GetGrain<IPocoTimerRequestGrain>(GetRandomGrainId());

            using var timerObserver = TimerDiagnosticObserver.Create();
            var numTimers = await grain.TestAllTimerOverloads();
            var waitForTimers = timerObserver.WaitForTickCountAsync(grain, numTimers, TimerDiagnosticTimeout);
            fixture.AdvanceTime(TimerOverloadDueTime);
            await waitForTimers;
            await grain.TestCompletedTimerResults();
        }

        /// <summary>
        /// Tests timer concurrency constraints in non-reentrant POCO grains.
        /// Verifies that POCO grains respect reentrancy settings, ensuring
        /// timer callbacks are properly serialized in non-reentrant grains
        /// regardless of the grain implementation style.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task NonReentrantGrainTimer_Test_Poco()
        {
            const string testName = "NonReentrantGrainTimer_Test";
            var delay = OneShotTimerDelay;

            using var timerObserver = TimerDiagnosticObserver.Create();
            var grain = GrainFactory.GetGrain<IPocoNonReentrantTimerCallGrain>(GetRandomGrainId());

            // Schedule multiple timers with the same delay
            await grain.StartTimer(testName, delay);
            await grain.StartTimer($"{testName}_1", delay);
            await grain.StartTimer($"{testName}_2", delay);

            // Invoke some non-interleaving methods while waiting for the timer callbacks.
            var externalTicks = await DriveExternalTickUntilTimerTicks(grain, timerObserver, delay, expectedTimerTicks: 3);

            var tickCount = await grain.GetTickCount();

            Assert.Equal(3 + externalTicks, tickCount);

            var err = await grain.GetException();
            Assert.Null(err); // Should be no exceptions during timer callback

            await grain.StopTimer(testName);
            await grain.StopTimer($"{testName}_1");
            await grain.StopTimer($"{testName}_2");
        }

        /// <summary>
        /// Tests dynamic timer period changes in POCO grains.
        /// Verifies that POCO grain timers support runtime modifications
        /// of timing parameters through the Change method, including
        /// edge cases and callback-initiated changes.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task GrainTimer_Change_Poco()
        {
            const string testName = nameof(GrainTimer_Change);
            TimeSpan delay = OneShotTimerDelay;

            using var timerObserver = TimerDiagnosticObserver.Create();
            var grain = GrainFactory.GetGrain<IPocoTimerCallGrain>(GetRandomGrainId());

            await grain.StartTimer(testName, delay);

            await AdvanceTimerToTickCountAsync(grain, timerObserver, delay, TimerCallbackDelay, expectedTickCount: 1);

            int tickCount = await grain.GetTickCount();
            Assert.Equal(1, tickCount);

            await grain.RestartTimer(testName, delay);

            await AdvanceTimerToTickCountAsync(grain, timerObserver, delay, TimerCallbackDelay, expectedTickCount: 2);

            tickCount = await grain.GetTickCount();
            Assert.Equal(2, tickCount);

            await grain.TestTimerChangeArguments();

            Exception err = await grain.GetException();
            Assert.Null(err); // Should be no exceptions during timer callback

            // Valid operations called from within a timer: updating the period and disposing the timer.
            var grain2 = GrainFactory.GetGrain<IPocoTimerCallGrain>(GetRandomGrainId());
            await grain2.StartTimer(testName, delay, "update_period");
            await AdvanceTimerToTickCountAsync(grain2, timerObserver, delay, TimerCallbackDelay, expectedTickCount: 1);
            Assert.Null(await grain2.GetException()); // Should be no exceptions during timer callback
            Assert.Equal(1, await grain2.GetTickCount());

            var grain3 = GrainFactory.GetGrain<IPocoTimerCallGrain>(GetRandomGrainId());
            await grain3.StartTimer(testName, delay, "dispose_timer");
            await AdvanceTimerToTickCountAsync(grain3, timerObserver, delay, TimerCallbackDelay, expectedTickCount: 1);
            Assert.Null(await grain3.GetException()); // Should be no exceptions during timer callback
            Assert.Equal(1, await grain3.GetTickCount());

            await grain.StopTimer(testName);
        }
    }
}
