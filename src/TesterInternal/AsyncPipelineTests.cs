using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTests.TestHelper;

namespace UnitTests.AsyncPrimitivesTests
{

    [TestClass]
    public class AsyncPipelineTests
    {
        private const int _iterationCount = 100;
        private const int _defaultPipelineCapacity = 2;

        public AsyncPipelineTests()
        {}

        [TestInitialize]
        public void TestInitialize()
        { }

        [TestCleanup]
        public void TestCleanup()
        { }

        [TestMethod, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void AsyncPipelineSimpleTest()
        {
            int step = 1000;
            var done = TimedCompletions(step, step, step);
            var pipeline = new AsyncPipeline(2);
            Stopwatch watch = new Stopwatch();
            watch.Start();
            pipeline.Add(done[0]);
            const int epsilon = 100;
            var elapsed0 = watch.ElapsedMilliseconds;
            Assert.IsTrue(elapsed0 < epsilon, elapsed0.ToString());
            pipeline.Add(done[2]);
            var elapsed1 = watch.ElapsedMilliseconds;
            Assert.IsTrue(elapsed1 < epsilon, elapsed1.ToString());
            pipeline.Add(done[1]);
            var elapsed2 = watch.ElapsedMilliseconds;
            Assert.IsTrue(step - epsilon <= elapsed2 && elapsed2 <= step + epsilon);
            pipeline.Wait();
            watch.Stop();
            Assert.IsTrue(3 * step - epsilon <= watch.ElapsedMilliseconds && watch.ElapsedMilliseconds <= 3 * step + epsilon);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void AsyncPipelineWaitTest()
        {
            Random rand = new Random(222);
            int started = 0;
            int finished1 = 0;
            int finished2 = 0;
            int numActions = 1000;
            Action action1 = (() => 
                {
                    lock (this) started++;
                    Thread.Sleep((int)(rand.NextDouble() * 100));
                    lock (this) finished1++;
                });
            Action action2 = (() =>
            {
                Thread.Sleep((int)(rand.NextDouble() * 100));
                lock (this) finished2++;
            });

            var pipeline = new AsyncPipeline(10);
            for (int i = 0; i < numActions; i++)
            {
                var async1 = Task.Run(action1);
                pipeline.Add(async1);
                var async2 = async1.ContinueWith(_ => action2());
                pipeline.Add(async2);
            }
            pipeline.Wait();
            Assert.AreEqual(numActions, started);
            Assert.AreEqual(numActions, finished1);
            Assert.AreEqual(numActions, finished2);
        }

        private static Task[] TimedCompletions(params int[] waits)
        {
            var result = new Task[waits.Length];
            var accum = 0;
            for (var i = 0; i < waits.Length; ++i)
            {
                accum += waits[i];
                result[i] = Task.Delay(accum);
            }
            return result;
        }
        
        [TestMethod, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public async Task AsyncPipelineSingleThreadedBlackBoxConsistencyTest()
        {
            await AsyncPipelineBlackBoxConsistencyTest(1);
        }

        private async Task AsyncPipelineBlackBoxConsistencyTest(int workerCount)
        {
            if (workerCount < 1)
                throw new ArgumentOutOfRangeException("You must specify at least one worker.", "workerCount");

            int loopCount = _iterationCount / workerCount;
            const double variance = 0.1;
            int expectedTasksCompleted = loopCount * workerCount;
            var delayLength = TimeSpan.FromSeconds(1);
            const int pipelineCapacity = _defaultPipelineCapacity;
            var pipeline = new AsyncPipeline(pipelineCapacity);
            int tasksCompleted = 0;
            // the following value is wrapped within an array to avoid a modified closure warning from ReSharper.
            int[] pipelineSize = { 0 };
            var capacityReached = new InterlockedFlag();

            Action workFunc =
                () =>
                {
                    var sz = Interlocked.Increment(ref pipelineSize[0]);
                    CheckPipelineState(sz, pipelineCapacity, capacityReached);
                    Task.Delay(delayLength).Wait();
                    Interlocked.Decrement(ref pipelineSize[0]);
                    Interlocked.Increment(ref tasksCompleted);
                };

            Action workerFunc =
                () =>
                {
                    for (var j = 0; j < loopCount; j++)
                    {
                        Task task = new Task(workFunc);
                        pipeline.Add(task, whiteBox: null);
                        task.Start();
                    }
                };

            Func<Task> monitorFunc =
                async () =>
                {
                    var delay = TimeSpan.FromSeconds(5);
                    while (tasksCompleted < expectedTasksCompleted)
                    {
                        Console.WriteLine("test in progress: tasksCompleted = {0}.", tasksCompleted);
                        await Task.Delay(delay);
                    }
                };

            var workers = new Task[workerCount];
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < workerCount; ++i)
                workers[i] = Task.Run(workerFunc);
            Task.Run(monitorFunc).Ignore();
            await Task.WhenAll(workers);
            pipeline.Wait();
            stopwatch.Stop();
            Assert.AreEqual(expectedTasksCompleted, tasksCompleted, "The test did not complete the expected number of tasks.");

            var targetTimeSec = expectedTasksCompleted * delayLength.TotalSeconds / pipelineCapacity;
            var minTimeSec = (1.0 - variance) * targetTimeSec;
            var maxTimeSec = (1.0 + variance) * targetTimeSec;
            var actualSec = stopwatch.Elapsed.TotalSeconds;
            Console.WriteLine(
                "Test finished in {0} sec, {1}% of target time {2} sec. Permitted variance is +/-{3}%",
                actualSec,
                actualSec / targetTimeSec * 100,
                targetTimeSec,
                variance * 100);

            Assert.IsTrue(capacityReached.IsSet, "Pipeline capacity not reached; the delay length probably is too short to be useful.");
            Assert.IsTrue(
                actualSec >= minTimeSec, 
                string.Format("The unit test completed too early ({0} sec < {1} sec).", actualSec, minTimeSec));
            Assert.IsTrue(
                actualSec <= maxTimeSec, 
                string.Format("The unit test completed too late ({0} sec > {1} sec).", actualSec, maxTimeSec));
        }

        private void CheckPipelineState(int size, int capacity, InterlockedFlag capacityReached)
        {
            Assert.IsTrue(size >= 0);
            Assert.IsTrue(capacity > 0);
            // a understood flaw of the current algorithm is that the capacity can be exceeded by one item. we've decided that this is acceptable and we allow it to happen.
            Assert.IsTrue(size <= capacity, string.Format("size ({0}) must be less than the capacity ({1})", size, capacity));
            if (capacityReached != null && size == capacity)
                capacityReached.TrySet();
        }
    }
}