using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;

using System.Collections.Generic;

// ReSharper disable ConvertToLambdaExpression

namespace UnitTests
{
    /// <summary>
    /// Summary description for UnitTest1
    /// </summary>
    [TestClass]
    public class Async_AsyncExecutorWithRetriesTests
    {
        internal static void CheckUnobservedPromises(List<string> unobservedPromises)
        {
            if (unobservedPromises.Count > 0)
            {
                for (int i = 0; i < unobservedPromises.Count; i++)
                {
                    string promise = unobservedPromises[i];
                    Console.WriteLine("Unobserved promise {0}: {1}", i, promise);
                }
                Assert.Fail("Found {0} unobserved promise(s) : [ {1} ]",
                                          unobservedPromises.Count,
                                          string.Join(" , \n", unobservedPromises));
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_1()
        {
            int counter = 0;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
                // ReSharper disable AccessToModifiedClosure
                Assert.AreEqual(counter, funcCounter);
                Console.WriteLine("Running for {0} time.", counter);
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
            Console.WriteLine("Value is {0}.", value);
            counter = 0;
            try
            {
                promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, 3, 3, null, errorFilter);
                value = promise.Result;
                Console.WriteLine("Value is {0}.", value);
            }
            catch (Exception)
            {
                return;
            }
            Assert.Fail("Should have thrown");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_2()
        {
            int counter = 0;
            const int countLimit = 5;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
// ReSharper disable AccessToModifiedClosure
                Assert.AreEqual(counter, funcCounter);
                Console.WriteLine("Running for {0} time.", counter);
                return Task.FromResult(++counter);
// ReSharper restore AccessToModifiedClosure
            });
            Func<int, int, bool> successFilter = ((int count, int i) => count != countLimit);

            int maxRetries = 10;
            int expectedRetries = countLimit;
            Task<int> promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, maxRetries, maxRetries, successFilter, null, Constants.INFINITE_TIMESPAN);
            int value = promise.Result;
            Console.WriteLine("Value={0} Counter={1} ExpectedRetries={2}", value, counter, expectedRetries);
            Assert.AreEqual(expectedRetries, value, "Returned value");
            Assert.AreEqual(counter, value, "Counter == Returned value");

            counter = 0;
            maxRetries = 3;
            expectedRetries = maxRetries;
            promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, maxRetries, maxRetries, successFilter, null);
            value = promise.Result;
            Console.WriteLine("Value={0} Counter={1} ExpectedRetries={2}", value, counter, expectedRetries);
            Assert.AreEqual(expectedRetries, value, "Returned value");
            Assert.AreEqual(counter, value, "Counter == Returned value");
        }

        [TestMethod, TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_3()
        {
            TimeSpan timeout = TimeSpan.FromMilliseconds(1000);
            int counter = 0;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
                Assert.AreEqual(counter, funcCounter);
                Console.WriteLine("Running for {0} time.", counter);
                return Task.FromResult(counter++);
            });
            Func<int, int, bool> successFilter = ((int count, int i) =>
            {
                return true;
            });

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            bool timeoutHappened = false;
            Task<int> promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, -1, -1, successFilter, null, timeout);
                //new ExponentialBackoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(100)));
            try
            {
                int value = promise.Result;
                Assert.Fail("Should have thrown " + value);
            }
            catch (Exception exc)
            {
                stopwatch.Stop();
                Exception baseExc = exc.GetBaseException();

                if (baseExc is TimeoutException)
                    timeoutHappened = true;
                else
                    Console.WriteLine(exc);
            }
            Assert.IsTrue(timeoutHappened);
            Assert.IsTrue(stopwatch.Elapsed >= timeout.Multiply(0.9) && stopwatch.Elapsed <= timeout.Multiply(2), "Elapsed = " + stopwatch.Elapsed + " timeout = " + timeout);
        }

        public void Async_AsyncExecutor_BackoffTest_1()
        {
            TimeSpan EXP_ERROR_BACKOFF_MIN = TimeSpan.FromSeconds(5);
            TimeSpan EXP_ERROR_BACKOFF_MAX = EXP_ERROR_BACKOFF_MIN.Multiply(2);
            TimeSpan EXP_CONTENTION_BACKOFF_MIN = TimeSpan.FromMilliseconds(100);
            TimeSpan EXP_CONTENTION_BACKOFF_MAX = TimeSpan.FromMilliseconds(5000);
            TimeSpan EXP_BACKOFF_STEP = TimeSpan.FromMilliseconds(500);

            AsyncExecutorWithRetries.ExecuteWithRetries(
                (int counter) =>
                {
                    Console.WriteLine("#{0}, time {1}.", counter, TraceLogger.PrintDate(DateTime.UtcNow));
                    throw new Exception();
                    //return false;
                },
                10, 
                10, // 5 retries
                (bool result, int i) => { return result == false; },   // if failed to Update on contention - retry   
                (Exception exc, int i) => { return true; },            // Retry on errors. 
                Constants.INFINITE_TIMESPAN,
                new ExponentialBackoff(EXP_CONTENTION_BACKOFF_MIN, EXP_CONTENTION_BACKOFF_MAX, EXP_BACKOFF_STEP), // how long to wait between sucessfull retries
                new ExponentialBackoff(EXP_ERROR_BACKOFF_MIN, EXP_ERROR_BACKOFF_MAX, EXP_BACKOFF_STEP)  // how long to wait between error retries
            ).Wait();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_4()
        {
            int counter = 0;
            int lastIteration = 0;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
                lastIteration = funcCounter;
                Assert.AreEqual(counter, funcCounter);
                Console.WriteLine("Running for {0} time.", counter);
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
            Console.WriteLine("Value={0} Counter={1} ExpectedRetries={2}", value, counter, 0);
            Assert.AreEqual(counter, value, "Counter == Returned value");
            Assert.AreEqual(counter, 1, "Counter == Returned value");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_5()
        {
            int counter = 0;
            int lastIteration = 0;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
                lastIteration = funcCounter;
                Assert.AreEqual(counter, funcCounter);
                Console.WriteLine("Running FUNC for {0} time.", counter);
                ++counter;
                throw new ArgumentException(counter.ToString(CultureInfo.InvariantCulture));
            });
            Func<Exception, int, bool> errorFilter = ((Exception exc, int i) =>
            {
                Console.WriteLine("Running ERROR FILTER for {0} time.", i);
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
                Console.WriteLine("baseExc.GetType()={0} Counter={1}", baseExc.GetType(), counter);
                Assert.AreEqual(counter, 3, "Counter == Returned value");
            }
        }
    }
}

// ReSharper restore ConvertToLambdaExpression
