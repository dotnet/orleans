using System;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    /// <summary>
    /// Tests that exceptions are correctly propagated.
    /// </summary>
    public class ExceptionPropagationTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly ITestOutputHelper output;

        public ExceptionPropagationTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task BasicExceptionPropagation()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => grain.ThrowsInvalidOperationException());

            output.WriteLine(exception.ToString());
            Assert.Equal("Test exception", exception.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public void ExceptionContainsOriginalStackTrace()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            // Explicitly using .Wait() instead of await the task to avoid any modification of the inner exception
            var aggEx = Assert.Throws<AggregateException>(
                () => grain.ThrowsInvalidOperationException().Wait());

            var exception = aggEx.InnerException;
            output.WriteLine(exception.ToString());
            Assert.IsAssignableFrom<InvalidOperationException>(exception);
            Assert.Equal("Test exception", exception.Message);
            Assert.Contains("ThrowsInvalidOperationException", exception.StackTrace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ExceptionContainsOriginalStackTraceWhenRethrowingLocally()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            try
            {
                // Use await to force the exception to be rethrown and validate that the remote stack trace is still present
                await grain.ThrowsInvalidOperationException();
                Assert.True(false, "should have thrown");
            }
            catch (InvalidOperationException exception)
            {
                output.WriteLine(exception.ToString());
                Assert.IsAssignableFrom<InvalidOperationException>(exception);
                Assert.Equal("Test exception", exception.Message);
                Assert.Contains("ThrowsInvalidOperationException", exception.StackTrace);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ExceptionPropagationDoesNotUnwrapAggregateExceptions()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => grain.ThrowsAggregateExceptionWrappingInvalidOperationException());

            var nestedEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Test exception", nestedEx.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ExceptionPropagationDoesNoFlattenAggregateExceptions()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => grain.ThrowsNestedAggregateExceptionsWrappingInvalidOperationException());

            var nestedAggEx = Assert.IsAssignableFrom<AggregateException>(exception.InnerException);
            var doubleNestedEx = Assert.IsAssignableFrom<InvalidOperationException>(nestedAggEx.InnerException);
            Assert.Equal("Test exception", doubleNestedEx.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task TaskCancelationPropagation()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => grain.Canceled());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task GrainForwardingExceptionPropagation()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var otherGrainId = GetRandomGrainId();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => grain.GrainCallToThrowsInvalidOperationException(otherGrainId));

            Assert.Equal("Test exception", exception.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task GrainForwardingExceptionPropagationDoesNotUnwrapAggregateExceptions()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var otherGrainId = GetRandomGrainId();
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => grain.GrainCallToThrowsAggregateExceptionWrappingInvalidOperationException(otherGrainId));

            var nestedEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Test exception", nestedEx.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task SynchronousExceptionThrownShouldResultInFaultedTask()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());

            // start the grain call but don't await it nor wrap in try/catch, to make sure it doesn't throw synchronously
            var grainCallTask = grain.ThrowsSynchronousInvalidOperationException();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grainCallTask);

            Assert.Equal("Test exception", exception.Message);
        }

        [Fact(Skip = "Implementation of issue #1378 is still pending"), TestCategory("BVT"), TestCategory("Functional")]
        public void ExceptionPropagationForwardsEntireAggregateException()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var grainCall = grain.ThrowsMultipleExceptionsAggregatedInFaultedTask();

            try
            {
                // use Wait() so that we get the entire AggregateException ('await' would just catch the first inner exception)
                // Do not use Assert.Throws to avoid any tampering of the AggregateException itself from the test framework
                grainCall.Wait();
                Assert.True(false, "Expected AggregateException");
            }
            catch (AggregateException exception)
            {
                output.WriteLine(exception.ToString());
                // make sure that all exceptions in the task are present, and not just the first one.
                Assert.Equal(2, exception.InnerExceptions.Count);
                var firstEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerExceptions[0]);
                Assert.Equal("Test exception 1", firstEx.Message);
                var secondEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerExceptions[1]);
                Assert.Equal("Test exception 2", secondEx.Message);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task SynchronousAggregateExceptionThrownShouldResultInFaultedTaskWithOriginalAggregateExceptionUnmodifiedAsInnerException()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());

            // start the grain call but don't await it nor wrap in try/catch, to make sure it doesn't throw synchronously
            var grainCallTask = grain.ThrowsSynchronousAggregateExceptionWithMultipleInnerExceptions();

            // assert that the faulted task has an inner exception of type AggregateException, which should be our original exception
            var exception = await Assert.ThrowsAsync<AggregateException>(() => grainCallTask);

            Assert.Equal("Test AggregateException message", exception.Message);
            // make sure that all exceptions in the task are present, and not just the first one.
            Assert.Equal(2, exception.InnerExceptions.Count);
            var firstEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerExceptions[0]);
            Assert.Equal("Test exception 1", firstEx.Message);
            var secondEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerExceptions[1]);
            Assert.Equal("Test exception 2", secondEx.Message);
        }
    }
}
