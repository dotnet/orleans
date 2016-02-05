using System;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.General
{
    /// <summary>
    /// Tests that exceptions are correctly propagated.
    /// </summary>
    public class ExceptionPropagationTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact(Skip = "Implementation of issue #1378 is still pending"), TestCategory("BVT"), TestCategory("Functional")]
        public async Task BasicExceptionPropagation()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => grain.ThrowsInvalidOperationException());

            Assert.Equal("Test exception", exception.Message);
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

        [Fact(Skip = "Implementation of issue #1378 is still pending"), TestCategory("BVT"), TestCategory("Functional")]
        public async Task ExceptionPropagationDoesNoFlattenAggregateExceptions()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => grain.ThrowsNestedAggregateExceptionsWrappingInvalidOperationException());

            var nestedAggEx = Assert.IsAssignableFrom<AggregateException>(exception.InnerException);
            var doubleNestedEx = Assert.IsAssignableFrom<InvalidOperationException>(nestedAggEx.InnerException);
            Assert.Equal("Test exception", doubleNestedEx.Message);
        }

        [Fact(Skip = "Implementation of issue #1378 is still pending"), TestCategory("BVT"), TestCategory("Functional")]
        public async Task TaskCancelationPropagation()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => grain.Canceled());
        }

        [Fact(Skip = "Implementation of issue #1378 is still pending"), TestCategory("BVT"), TestCategory("Functional")]
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
    }
}
