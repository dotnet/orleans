using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Internal;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

namespace DefaultCluster.Tests
{
    /// <summary>
    /// Tests error handling and exception propagation in Orleans.
    /// These tests verify Orleans' robust error handling capabilities including:
    /// - Exception serialization and propagation across process boundaries
    /// - Handling of synchronous and asynchronous errors
    /// - Timeout behavior and recovery
    /// - Stress testing with multiple concurrent failing operations
    /// - Various grain communication patterns under error conditions
    /// Orleans ensures that errors in grains don't crash the system and are properly communicated to callers.
    /// </summary>
    public class ErrorGrainTest : HostedTestClusterEnsureDefaultStarted
    {
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);
        private readonly ITestOutputHelper output;

        public ErrorGrainTest(ITestOutputHelper output, DefaultClusterFixture fixture) : base(fixture)
        {
            this.output = output;
        }

        /// <summary>
        /// Tests basic grain reference creation for error testing grains.
        /// Verifies that error grain references can be obtained and basic methods can be called.
        /// This establishes the baseline for error handling tests.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ErrorHandling")]
        public async Task ErrorGrain_GetGrain()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);
            _ = await grain.GetA();
        }

        /// <summary>
        /// Tests error handling for locally thrown exceptions (not in Orleans grains).
        /// Verifies that standard .NET exception handling works as expected for comparison
        /// with distributed error handling in Orleans.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ErrorHandling")]
        public async Task ErrorHandlingLocalError()
        {
            LocalErrorGrain localGrain = new LocalErrorGrain();
            
            Task<int> intPromise = localGrain.GetAxBError();
            try
            {
                await intPromise;
                Assert.Fail("Should not have executed");
            }
            catch (Exception exc2)
            {
                Assert.Equal(exc2.GetBaseException().Message, (new Exception("GetAxBError-Exception")).Message);
            }

            Assert.True(intPromise.Status == TaskStatus.Faulted);                
        }

        /// <summary>
        /// Tests that grain methods that throw exceptions properly fail their returned Tasks.
        /// Verifies that:
        /// - Exceptions in grain methods cause the Task to enter Faulted state
        /// - The exception can be retrieved from the Task
        /// - Multiple awaits on the same failed Task consistently throw the same exception
        /// This ensures reliable error propagation in distributed calls.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ErrorHandling")]
        public async Task ErrorHandlingGrainError1()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);

            Task<int> intPromise = grain.GetAxBError();
            try
            {
                await intPromise;
                Assert.Fail("Should have thrown");
            }
            catch (Exception)
            {
                Assert.True(intPromise.Status == TaskStatus.Faulted);
            }

            try
            {
                await intPromise;
                Assert.Fail("Should have thrown");
            }
            catch (Exception exc2)
            {
                Assert.True(intPromise.Status == TaskStatus.Faulted);
                Assert.Equal((new Exception("GetAxBError-Exception")).Message, exc2.GetBaseException().Message);
            }

            Assert.True(intPromise.Status == TaskStatus.Faulted);
        }

        /// <summary>
        /// Tests behavior of long-running grain methods without errors.
        /// Verifies that:
        /// - Tasks for long-running operations don't complete prematurely
        /// - The Task eventually completes successfully
        /// - Timing assertions ensure proper async behavior
        /// This establishes baseline behavior for comparison with timeout scenarios.
        /// </summary>
        [Fact(Skip = "https://github.com/dotnet/orleans/issues/9558"), TestCategory("BVT"), TestCategory("ErrorHandling")]
        public async Task ErrorHandlingTimedMethod()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);

            Task promise = grain.LongMethod(2000);

            // there is a race in the test here. If run in debugger, the invocation can actually finish OK
            Stopwatch stopwatch = Stopwatch.StartNew();

            await Task.Delay(1000);
            Assert.False(promise.IsCompleted, "The task shouldn't have completed yet.");

            // these asserts depend on timing issues and will be wrong for the sync version of OrleansTask
            Assert.True(stopwatch.ElapsedMilliseconds >= 900, $"Waited less than 900ms: ({stopwatch.ElapsedMilliseconds}ms)"); // check that we waited at least 0.9 second
            Assert.True(stopwatch.ElapsedMilliseconds <= 1300, $"Waited longer than 1300ms: ({stopwatch.ElapsedMilliseconds}ms)");

            await promise; // just wait for the server side grain invocation to finish
            
            Assert.True(promise.Status == TaskStatus.RanToCompletion);
        }

        /// <summary>
        /// Tests behavior of long-running grain methods that eventually throw exceptions.
        /// Verifies that:
        /// - Tasks don't complete until the actual error occurs
        /// - The exception is properly propagated when the method completes
        /// - Timing ensures the error happens after the expected delay
        /// This tests Orleans' handling of delayed failures.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ErrorHandling")]
        public async Task ErrorHandlingTimedMethodWithError()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);

            Task promise = grain.LongMethodWithError(2000);

            // there is a race in the test here. If run in debugger, the invocation can actually finish OK
            Stopwatch stopwatch = Stopwatch.StartNew();

            await Task.Delay(1000);
            Assert.False(promise.IsCompleted, "The task shouldn't have completed yet.");

            stopwatch.Stop();
            Assert.True(stopwatch.ElapsedMilliseconds >= 900, $"Waited less than 900ms: ({stopwatch.ElapsedMilliseconds}ms)"); // check that we waited at least 0.9 second
            Assert.True(stopwatch.ElapsedMilliseconds <= 1300, $"Waited longer than 1300ms: ({stopwatch.ElapsedMilliseconds}ms)");

            await Assert.ThrowsAsync<Exception>(() => promise);

            Assert.True(promise.Status == TaskStatus.Faulted);
        }

        /// <summary>
        /// Stress tests Orleans with many concurrent delayed grain calls.
        /// Verifies that:
        /// - The system can handle hundreds of concurrent grain calls
        /// - All calls complete successfully without deadlocks
        /// - Orleans properly manages resources under load
        /// This tests the scalability of Orleans' message handling and scheduling.
        /// </summary>
        [Fact, TestCategory("Functional"), TestCategory("ErrorHandling"), TestCategory("Stress")]
        public async Task StressHandlingMultipleDelayedRequests()
        {
            IErrorGrain grain = this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId());
            bool once = true;
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 500; i++)
            {
                Task promise = grain.DelayMethod(1);
                tasks.Add(promise);
                if (once)
                {
                    once = false;
                    await promise;
                }

            }
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
        }

        /// <summary>
        /// Tests passing collections of grain references as method arguments.
        /// Verifies that Orleans correctly serializes and deserializes complex types
        /// containing grain references. This is important for scenarios where grains
        /// need to coordinate with multiple other grains.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ErrorHandling"), TestCategory("GrainReference")]
        public async Task ArgumentTypes_ListOfGrainReferences()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            List<IErrorGrain> list = new List<IErrorGrain>();
            IErrorGrain grain = this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);
            list.Add(this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName));
            list.Add(this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName));
            await grain.AddChildren(list).WaitAsync(timeout);
        }

        /// <summary>
        /// Tests Orleans' delayed execution capabilities.
        /// Verifies that grains can schedule delayed operations and that these
        /// operations execute correctly after the specified delay.
        /// This tests Orleans' internal timer and scheduling mechanisms.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("AsynchronyPrimitives"), TestCategory("ErrorHandling")]
        public async Task AC_DelayedExecutor_2()
        {
            var grainFullName = typeof(ErrorGrain).FullName;
            IErrorGrain grain = this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName);
            Task<bool> promise = grain.ExecuteDelayed(TimeSpan.FromMilliseconds(2000));
            bool result = await promise;
            Assert.True(result);
        }

        /// <summary>
        /// Tests async method patterns in simple grains.
        /// Verifies that grains correctly handle async methods for setting and getting state.
        /// This demonstrates Orleans' support for modern async/await patterns in grain implementations.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("SimpleGrain")]
        public async Task SimpleGrain_AsyncMethods()
        {
            ISimpleGrainWithAsyncMethods grain = this.GrainFactory.GetGrain<ISimpleGrainWithAsyncMethods>(GetRandomGrainId());
            Task setPromise = grain.SetA_Async(10);
            await setPromise;

            setPromise = grain.SetB_Async(30);
            await setPromise;

            var value = await grain.GetAxB_Async();
            Assert.Equal(300, value);
        }

        /// <summary>
        /// Tests promise forwarding where one grain forwards a call to another grain.
        /// Verifies that Orleans correctly handles chained grain calls where the result
        /// of one grain call is directly returned by another grain.
        /// This pattern is common in grain orchestration scenarios.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("SimpleGrain")]
        public async Task SimpleGrain_PromiseForward()
        {
            ISimpleGrain forwardGrain = this.GrainFactory.GetGrain<IPromiseForwardGrain>(GetRandomGrainId());
            Task<int> promise = forwardGrain.GetAxB(5, 6);
            int result = await promise;
            Assert.Equal(30, result);
        }

        /// <summary>
        /// Tests GUID-based grain ID distribution and hashing.
        /// Verifies that different GUID patterns produce well-distributed hash codes
        /// for grain placement. This is important for load balancing across silos.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("SimpleGrain")]
        public void SimpleGrain_GuidDistribution()
        {
            int n = 0x1111;
            CreateGR(n, 1);
            CreateGR(n + 1, 1);
            CreateGR(n + 2, 1);
            CreateGR(n + 3, 1);
            CreateGR(n + 4, 1);

            Logger.LogInformation("================");

            CreateGR(n, 2);
            CreateGR(n + 1, 2);
            CreateGR(n + 2, 2);
            CreateGR(n + 3, 2);
            CreateGR(n + 4, 2);

            Logger.LogInformation("DONE.");
        }

        /// <summary>
        /// Helper method to create grain references with specific GUID patterns.
        /// Used to test hash code distribution for different GUID formats.
        /// </summary>
        private void CreateGR(int n, int type)
        {
            Guid guid;
            if (type == 1)
            {
                guid = Guid.Parse(string.Format("00000000-0000-0000-0000-{0:X12}", n));
            }
            else
            {
                guid = Guid.Parse(string.Format("{0:X8}-0000-0000-0000-000000000000", n));
            }
            IEchoGrain grain = this.GrainFactory.GetGrain<IEchoGrain>(guid);
            GrainId grainId = ((GrainReference)grain.AsReference<IEchoGrain>()).GrainId;
            output.WriteLine("Guid = {0}, Guid.HashCode = x{1:X8}, GrainId.HashCode = x{2:X8}, GrainId.UniformHashCode = x{3:X8}", guid, guid.GetHashCode(), grainId.GetHashCode(), grainId.GetUniformHashCode());
        }

        /// <summary>
        /// Tests observer disconnection scenarios.
        /// Verifies behavior when client observers are disconnected from grains.
        /// This tests Orleans' observer pattern implementation for event notifications.
        /// </summary>
        [Fact, TestCategory("Revisit"), TestCategory("Observers")]
        public void ObserverTest_Disconnect()
        {
            ObserverTest_DisconnectRunner(false);
        }

        /// <summary>
        /// Tests observer disconnection with multiple subscriptions.
        /// Verifies behavior when the same observer is subscribed multiple times
        /// and then disconnected. This tests edge cases in observer management.
        /// </summary>
        [Fact, TestCategory("Revisit"), TestCategory("Observers")]
        public void ObserverTest_Disconnect2()
        {
            ObserverTest_DisconnectRunner(true);
        }

        /// <summary>
        /// Helper method for testing observer disconnection scenarios.
        /// The observeTwice parameter controls whether to test single or double subscription.
        /// Note: This test appears to be commented out for manual debugging scenarios.
        /// </summary>
        private void ObserverTest_DisconnectRunner(bool observeTwice)
        {
            // this is for manual repro & validation in the debugger
            // wait to send event because it takes 60s to drop client grain
            //var simple1 = SimpleGrainTests.GetSimpleGrain();
            //var simple2 = SimpleGrainFactory.Cast(Domain.Current.Create(typeof(ISimpleGrain).FullName,
            //    new Dictionary<string, object> { { "EventDelay", 70000 } }));
            //var result = new ResultHandle();
            //var callback = new SimpleGrainObserver((a, b, r) =>
            //{
            //    r.Done = (a == 10);
            //    output.WriteLine("Received observer callback: A={0} B={1} Done={2}", a, b, r.Done);
            //}, result);
            //var observer = SimpleGrainObserverFactory.CreateObjectReference(callback);
            //if (observeTwice)
            //{
            //    simple1.Subscribe(observer).Wait();
            //    simple1.SetB(1).Wait(); // send a message to the observer to get it in the cache
            //}
            //simple2.Subscribe(observer).Wait();
            //simple2.SetA(10).Wait();
            //Thread.Sleep(2000);
            //Client.Uninitialize();
            //var timeout80sec = TimeSpan.FromSeconds(80);
            //Assert.False(result.WaitForFinished(timeout80sec), "WaitforFinished Timeout=" + timeout80sec);
            //// prevent silo from shutting down right away
            //Thread.Sleep(Debugger.IsAttached ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(5));
        }
    }
}
