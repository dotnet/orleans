using System;
using System.Globalization;
using System.Threading.Tasks;
using Orleans.Internal;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable ConvertToLambdaExpression

namespace NonSilo.Tests
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
            var counter = 0;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
                // ReSharper disable AccessToModifiedClosure
                Assert.Equal(counter, funcCounter);
                this.output.WriteLine("Running for {0} time.", counter);
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

            var promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, 10, 10, null, errorFilter);
            var value = promise.Result;
            this.output.WriteLine("Value is {0}.", value);
            counter = 0;
            try
            {
                promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, 3, 3, null, errorFilter);
                value = promise.Result;
                this.output.WriteLine("Value is {0}.", value);
            }
            catch (Exception)
            {
                return;
            }
            Assert.True(false,"Should have thrown");
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_2()
        {
            var counter = 0;
            const int countLimit = 5;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
// ReSharper disable AccessToModifiedClosure
                Assert.Equal(counter, funcCounter);
                this.output.WriteLine("Running for {0} time.", counter);
                return Task.FromResult(++counter);
// ReSharper restore AccessToModifiedClosure
            });
            Func<int, int, bool> successFilter = ((int count, int i) => count != countLimit);

            var maxRetries = 10;
            var expectedRetries = countLimit;
            var promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, maxRetries, maxRetries, successFilter, null, Constants.INFINITE_TIMESPAN);
            var value = promise.Result;
            this.output.WriteLine("Value={0} Counter={1} ExpectedRetries={2}", value, counter, expectedRetries);
            Assert.Equal(expectedRetries, value); // "Returned value"
            Assert.Equal(counter, value); // "Counter == Returned value"

            counter = 0;
            maxRetries = 3;
            expectedRetries = maxRetries;
            promise = AsyncExecutorWithRetries.ExecuteWithRetries(myFunc, maxRetries, maxRetries, successFilter, null);
            value = promise.Result;
            this.output.WriteLine("Value={0} Counter={1} ExpectedRetries={2}", value, counter, expectedRetries);
            Assert.Equal(expectedRetries, value); // "Returned value"
            Assert.Equal(counter, value); // "Counter == Returned value"
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_4()
        {
            var counter = 0;
            var lastIteration = 0;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
                lastIteration = funcCounter;
                Assert.Equal(counter, funcCounter);
                this.output.WriteLine("Running for {0} time.", counter);
                return Task.FromResult(++counter);
            });
            Func<Exception, int, bool> errorFilter = ((Exception exc, int i) =>
            {
                Assert.Equal(lastIteration, i);
                Assert.True(false, "Should not be called");
                return true;
            });

            var maxRetries = 5;
            var promise = AsyncExecutorWithRetries.ExecuteWithRetries(
                myFunc, 
                maxRetries, 
                errorFilter,
                default(TimeSpan),
                new FixedBackoff(TimeSpan.FromSeconds(1)));

            var value = promise.Result;
            this.output.WriteLine("Value={0} Counter={1} ExpectedRetries={2}", value, counter, 0);
            Assert.Equal(counter, value);
            Assert.Equal(1, counter);
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Async_AsyncExecutorWithRetriesTest_5()
        {
            var counter = 0;
            var lastIteration = 0;
            Func<int, Task<int>> myFunc = ((int funcCounter) =>
            {
                lastIteration = funcCounter;
                Assert.Equal(counter, funcCounter);
                this.output.WriteLine("Running FUNC for {0} time.", counter);
                ++counter;
                throw new ArgumentException(counter.ToString(CultureInfo.InvariantCulture));
            });
            Func<Exception, int, bool> errorFilter = ((Exception exc, int i) =>
            {
                this.output.WriteLine("Running ERROR FILTER for {0} time.", i);
                Assert.Equal(lastIteration, i);
                if (i==0 || i==1)
                    return true;
                else if (i == 2)
                    throw exc;
                else
                    return false;
            });

            var maxRetries = 5;
            var promise = AsyncExecutorWithRetries.ExecuteWithRetries(
                myFunc,
                maxRetries,
                errorFilter,
                default(TimeSpan),
                new FixedBackoff(TimeSpan.FromSeconds(1)));
            try
            {
                var value = promise.Result;
                Assert.True(false,"Should have thrown");
            }
            catch (Exception exc)
            {
                var baseExc = exc.GetBaseException();
                Assert.Equal(typeof(ArgumentException), baseExc.GetType());
                this.output.WriteLine("baseExc.GetType()={0} Counter={1}", baseExc.GetType(), counter);
                Assert.Equal(3, counter); // "Counter == Returned value"
            }
        }
    }
}

// ReSharper restore ConvertToLambdaExpression
