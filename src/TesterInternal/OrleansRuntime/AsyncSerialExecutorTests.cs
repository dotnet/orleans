using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Xunit;

namespace UnitTests.OrleansRuntime
{
    public class AsyncSerialExecutorTestsFixture
    {
        public TraceLogger Logger;
        
        public int OperationsInProgress;

        public AsyncSerialExecutorTestsFixture()
        {
            TraceLogger.Initialize(new NodeConfiguration());
            Logger = TraceLogger.GetLogger("AsyncSerialExecutorTests", TraceLogger.LoggerType.Application);
        }
    }

    public class AsyncSerialExecutorTests : ICollectionFixture<AsyncSerialExecutorTestsFixture>
    {
        private AsyncSerialExecutorTestsFixture _fixture;

        public AsyncSerialExecutorTests(AsyncSerialExecutorTestsFixture fixture)
        {
            _fixture = fixture;
        }

        private SafeRandom random;

        [Fact, TestCategory("Functional"), TestCategory("Async")]
        public async Task AsyncSerialExecutorTests_Small()
        {
            AsyncSerialExecutor executor = new AsyncSerialExecutor();
            List<Task> tasks = new List<Task>();
            random = new SafeRandom();
            _fixture.OperationsInProgress = 0;

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
                _fixture.Logger.Info("Submitting Task {0}.", capture);
                tasks.Add(executor.AddNext(() => Operation(capture)));
            }
            await Task.WhenAll(tasks);
        }

        [Fact, TestCategory("Functional"), TestCategory("Async")]
        public async Task AsyncSerialExecutorTests_ParallelSubmit()
        {
            AsyncSerialExecutor executor = new AsyncSerialExecutor();
            random = new SafeRandom();
            List<Task> tasks = new List<Task>();
            List<Task> enqueueTasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                int capture = i;
                enqueueTasks.Add(
                    Task.Run(() =>
                    {
                        _fixture.Logger.Info("Submitting Task {0}.", capture);
                        tasks.Add(executor.AddNext(() => Operation(capture)));
                    }));
            }
            await Task.WhenAll(enqueueTasks);
            await Task.WhenAll(tasks);
        }

        private async Task Operation(int opNumber)
        {
            if (_fixture.OperationsInProgress > 0) Assert.Fail("1: Operation {0} found {1} operationsInProgress.", opNumber, _fixture.OperationsInProgress);
            _fixture.OperationsInProgress++;
            var delay = random.NextTimeSpan(TimeSpan.FromSeconds(2));

            _fixture.Logger.Info("Task {0} Staring", opNumber);
            await Task.Delay(delay);
            if (_fixture.OperationsInProgress != 1) Assert.Fail("2: Operation {0} found {1} operationsInProgress.", opNumber, _fixture.OperationsInProgress);

            _fixture.Logger.Info("Task {0} after first delay", opNumber);
            await Task.Delay(delay);
            if (_fixture.OperationsInProgress != 1) Assert.Fail("3: Operation {0} found {1} operationsInProgress.", opNumber, _fixture.OperationsInProgress);

            _fixture.OperationsInProgress--;
            _fixture.Logger.Info("Task {0} Done", opNumber);
        }
    }
}
