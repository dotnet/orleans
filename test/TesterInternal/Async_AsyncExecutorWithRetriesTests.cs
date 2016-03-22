﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable ConvertToLambdaExpression

namespace UnitTests
{
    /// <summary>
    /// Summary description for UnitTest1
    /// </summary>
    public class Async_AsyncExecutorWithRetriesTests
    {
        private readonly ITestOutputHelper output;

        public Async_AsyncExecutorWithRetriesTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_1()
        {
            int counter = 0;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
                // ReSharper disable AccessToModifiedClosure
                Assert.AreEqual(counter, funcCounter);
                output.WriteLine("Running for {0} time.", counter);
                counter++;
                if (counter == 5)
                    return Task.FromResult(28);
                else
                    throw new ArgumentException("Wrong arg!");
                // ReSharper restore AccessToModifiedClosure
            });
            Func<Exception, int, bool> errorFilter = ((Exception exc, int i) =>
            {
                return true;
            });

            Task<int> promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, 10, 10, null, errorFilter);
            int value = promise.Result;
            output.WriteLine("Value is {0}.", value);
            counter = 0;
            try
            {
                promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, 3, 3, null, errorFilter);
                value = promise.Result;
                output.WriteLine("Value is {0}.", value);
            }
            catch (Exception)
            {
                return;
            }
            Assert.Fail("Should have thrown");
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_2()
        {
            int counter = 0;
            const int countLimit = 5;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
// ReSharper disable AccessToModifiedClosure
                Assert.AreEqual(counter, funcCounter);
                output.WriteLine("Running for {0} time.", counter);
                return Task.FromResult(++counter);
// ReSharper restore AccessToModifiedClosure
            });
            Func<int, int, bool> successFilter = ((int count, int i) => count != countLimit);

            int maxRetries = 10;
            int expectedRetries = countLimit;
            Task<int> promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, maxRetries, maxRetries, successFilter, null, Constants.INFINITE_TIMESPAN);
            int value = promise.Result;
            output.WriteLine("Value={0} Counter={1} ExpectedRetries={2}", value, counter, expectedRetries);
            Assert.AreEqual(expectedRetries, value, "Returned value");
            Assert.AreEqual(counter, value, "Counter == Returned value");

            counter = 0;
            maxRetries = 3;
            expectedRetries = maxRetries;
            promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, maxRetries, maxRetries, successFilter, null);
            value = promise.Result;
            output.WriteLine("Value={0} Counter={1} ExpectedRetries={2}", value, counter, expectedRetries);
            Assert.AreEqual(expectedRetries, value, "Returned value");
            Assert.AreEqual(counter, value, "Counter == Returned value");
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_4()
        {
            int counter = 0;
            int lastIteration = 0;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
                lastIteration = funcCounter;
                Assert.AreEqual(counter, funcCounter);
                output.WriteLine("Running for {0} time.", counter);
                return Task.FromResult(++counter);
            });
            Func<Exception, int, bool> errorFilter = ((Exception exc, int i) =>
            {
                Assert.AreEqual(lastIteration, i);
                Assert.Fail("Should not be called");
                return true;
            });

            int maxRetries = 5;
            Task<int> promise = AsyncExecutorWithRetries.ExecuteWithRetries(
                myFunc, 
                maxRetries, 
                errorFilter,
                default(TimeSpan),
                new FixedBackoff(TimeSpan.FromSeconds(1)));

            int value = promise.Result;
            output.WriteLine("Value={0} Counter={1} ExpectedRetries={2}", value, counter, 0);
            Assert.AreEqual(counter, value, "Counter == Returned value");
            Assert.AreEqual(counter, 1, "Counter == Returned value");
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_5()
        {
            int counter = 0;
            int lastIteration = 0;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
                lastIteration = funcCounter;
                Assert.AreEqual(counter, funcCounter);
                output.WriteLine("Running FUNC for {0} time.", counter);
                ++counter;
                throw new ArgumentException(counter.ToString(CultureInfo.InvariantCulture));
            });
            Func<Exception, int, bool> errorFilter = ((Exception exc, int i) =>
            {
                output.WriteLine("Running ERROR FILTER for {0} time.", i);
                Assert.AreEqual(lastIteration, i);
                if (i==0 || i==1)
                    return true;
                else if (i == 2)
                    throw exc;
                else
                    return false;
            });

            int maxRetries = 5;
            Task<int> promise = AsyncExecutorWithRetries.ExecuteWithRetries(
                myFunc,
                maxRetries,
                errorFilter,
                default(TimeSpan),
                new FixedBackoff(TimeSpan.FromSeconds(1)));
            try
            {
                int value = promise.Result;
                Assert.Fail("Should have thrown");
            }
            catch (Exception exc)
            {
                Exception baseExc = exc.GetBaseException();
                Assert.AreEqual(baseExc.GetType(), typeof(ArgumentException));
                output.WriteLine("baseExc.GetType()={0} Counter={1}", baseExc.GetType(), counter);
                Assert.AreEqual(counter, 3, "Counter == Returned value");
            }
        }
    }
}

// ReSharper restore ConvertToLambdaExpression
