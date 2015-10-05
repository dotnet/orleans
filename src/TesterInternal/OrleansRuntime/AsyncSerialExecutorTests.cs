/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace UnitTests.OrleansRuntime
{
    [TestClass]
    public class AsyncSerialExecutorTests
    {
        private static TraceLogger logger;
        private SafeRandom random;
        private int operationsInProgress;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            TraceLogger.Initialize(new NodeConfiguration());
            logger = TraceLogger.GetLogger("AsyncSerialExecutorTests", TraceLogger.LoggerType.Application);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Async")]
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

        [TestMethod, TestCategory("Functional"), TestCategory("Async")]
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

        [TestMethod, TestCategory("Functional"), TestCategory("Async")]
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
                        logger.Info("Submitting Task {0}.", capture);
                        tasks.Add(executor.AddNext(() => Operation(capture)));
                    }));
            }
            await Task.WhenAll(enqueueTasks);
            await Task.WhenAll(tasks);
        }

        private async Task Operation(int opNumber)
        {
            if (operationsInProgress > 0) Assert.Fail("1: Operation {0} found {1} operationsInProgress.", opNumber, operationsInProgress);
            operationsInProgress++;
            var delay = random.NextTimeSpan(TimeSpan.FromSeconds(2));

            logger.Info("Task {0} Staring", opNumber);
            await Task.Delay(delay);
            if (operationsInProgress != 1) Assert.Fail("2: Operation {0} found {1} operationsInProgress.", opNumber, operationsInProgress);

            logger.Info("Task {0} after first delay", opNumber);
            await Task.Delay(delay);
            if (operationsInProgress != 1) Assert.Fail("3: Operation {0} found {1} operationsInProgress.", opNumber, operationsInProgress);

            operationsInProgress--;
            logger.Info("Task {0} Done", opNumber);
        }
    }
}
