using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Xunit;

namespace UnitTests.OrleansRuntime
{
    public class AsyncSerialExecutorTests
    {
        public Logger logger;
        private SafeRandom random;
        public int operationsInProgress;

        public AsyncSerialExecutorTests()
        {
            LogManager.Initialize(new NodeConfiguration());
            logger = LogManager.GetLogger("AsyncSerialExecutorTests", LoggerType.Application);
        }

        [Fact, TestCategory("Functional"), TestCategory("Async")]
        public async Task AsyncSerialExecutorTests_Small()
        {
            AsyncSerialExecutor executor = new AsyncSerialExecutor();
            List<Task> tasks = new List<Task>();
            random = new SafeRandom();
            operationsInProgress = 0;

            tasks.Add(executor.AddNext(() => Operation(1)));
            tasks.Add(executor.AddNext(() => Operation(2)));
            tasks.Add(executor.AddNext(() => Operation(3)));

            await Task.WhenAll(tasks);
        }

        [Fact, TestCategory("Functional"), TestCategory("Async")]
        public async Task AsyncSerialExecutorTests_SerialSubmit()
        {
            AsyncSerialExecutor executor = new AsyncSerialExecutor();
            random = new SafeRandom();
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                int capture = i;
                logger.Info("Submitting Task {0}.", capture);
                tasks.Add(executor.AddNext(() => Operation(capture)));
            }
            await Task.WhenAll(tasks);
        }

        [Fact, TestCategory("Functional"), TestCategory("Async")]
        public async Task AsyncSerialExecutorTests_ParallelSubmit()
        {
            AsyncSerialExecutor executor = new AsyncSerialExecutor();
            random = new SafeRandom();
            ConcurrentStack<Task> tasks = new ConcurrentStack<Task>();
            List<Task> enqueueTasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                int capture = i;
                enqueueTasks.Add(
                    Task.Run(() =>
                    {
                        logger.Info("Submitting Task {0}.", capture);
                        tasks.Push(executor.AddNext(() => Operation(capture)));
                    }));
            }
            await Task.WhenAll(enqueueTasks);
            await Task.WhenAll(tasks);
        }

        private async Task Operation(int opNumber)
        {
            if (operationsInProgress > 0) Assert.True(false, $"1: Operation {opNumber} found {operationsInProgress} operationsInProgress.");
            operationsInProgress++;
            var delay = random.NextTimeSpan(TimeSpan.FromSeconds(2));

            logger.Info("Task {0} Staring", opNumber);
            await Task.Delay(delay);
            if (operationsInProgress != 1) Assert.True(false, $"2: Operation {opNumber} found {operationsInProgress} operationsInProgress.");

            logger.Info("Task {0} after first delay", opNumber);
            await Task.Delay(delay);
            if (operationsInProgress != 1) Assert.True(false, $"3: Operation {opNumber} found {operationsInProgress} operationsInProgress.");

            operationsInProgress--;
            logger.Info("Task {0} Done", opNumber);
        }
    }
}
