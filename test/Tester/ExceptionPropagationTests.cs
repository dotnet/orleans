using Orleans;

namespace UnitTests.General
{
    using System;
    using System.Threading.Tasks;
    using TestExtensions;

    using UnitTests.GrainInterfaces;
    using UnitTests.Grains;

    using Xunit;
    using Xunit.Abstractions;
    
    /// <summary>
    /// Tests that exceptions are correctly propagated.
    /// </summary>
    public class ExceptionPropagationTests : OrleansTestingBase, IClassFixture<ExceptionPropagationTests.Fixture>
    {
        private readonly ITestOutputHelper output;
        private readonly Fixture fixture;

        public ExceptionPropagationTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
        }

        public class Fixture : BaseTestClusterFixture
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task BasicExceptionPropagation()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => grain.ThrowsInvalidOperationException());

            output.WriteLine(exception.ToString());
            Assert.Equal("Test exception", exception.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public void ExceptionContainsOriginalStackTrace()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            
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
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
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
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => grain.ThrowsAggregateExceptionWrappingInvalidOperationException());

            var nestedEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Test exception", nestedEx.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ExceptionPropagationDoesNoFlattenAggregateExceptions()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => grain.ThrowsNestedAggregateExceptionsWrappingInvalidOperationException());

            var nestedAggEx = Assert.IsAssignableFrom<AggregateException>(exception.InnerException);
            var doubleNestedEx = Assert.IsAssignableFrom<InvalidOperationException>(nestedAggEx.InnerException);
            Assert.Equal("Test exception", doubleNestedEx.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task TaskCancelationPropagation()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => grain.Canceled());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task GrainForwardingExceptionPropagation()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var otherGrainId = GetRandomGrainId();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => grain.GrainCallToThrowsInvalidOperationException(otherGrainId));

            Assert.Equal("Test exception", exception.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task GrainForwardingExceptionPropagationDoesNotUnwrapAggregateExceptions()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var otherGrainId = GetRandomGrainId();
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => grain.GrainCallToThrowsAggregateExceptionWrappingInvalidOperationException(otherGrainId));

            var nestedEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Test exception", nestedEx.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task SynchronousExceptionThrownShouldResultInFaultedTask()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());

            // start the grain call but don't await it nor wrap in try/catch, to make sure it doesn't throw synchronously
            var grainCallTask = grain.ThrowsSynchronousInvalidOperationException();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grainCallTask);

            Assert.Equal("Test exception", exception.Message);

            var grainCallTask2 = grain.ThrowsSynchronousInvalidOperationException();
            var exception2 = await Assert.ThrowsAsync<InvalidOperationException>(() => grainCallTask2);
            Assert.Equal("Test exception", exception2.Message);
        }

        [Fact(Skip = "Implementation of issue #1378 is still pending"), TestCategory("BVT"), TestCategory("Functional")]
        public void ExceptionPropagationForwardsEntireAggregateException()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
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
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());

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

        /// <summary>
        /// Tests that when a grain cannot deserialize a request from a client, an exception is promptly propagated from the grain to the caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainToClient_DeserializationFailure()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMessageSerializationGrain>(GetRandomGrainId());
            
            var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => grain.Send(new UndeserializableType(2)));
            Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
        }

        /// <summary>
        /// Tests that when a grain cannot serialize a response to a client, an exception is promptly propagated from the grain to the caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainToClient_SerializationFailure()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMessageSerializationGrain>(GetRandomGrainId());
            
            var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => grain.Get());
            Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
        }

        /// <summary>
        /// Tests that when a grain cannot deserialize a request from another silo, an exception is promptly propagated from the grain to the caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainToGrain_DeserializationFailure()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMessageSerializationGrain>(GetRandomGrainId());
            
            var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => grain.SendToOtherSilo());
            Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
        }

        /// <summary>
        /// Tests that when a grain cannot serialize a response to another silo, an exception is promptly propagated from the grain to the caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainToGrain_SerializationFailure()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMessageSerializationGrain>(GetRandomGrainId());
            
            var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => grain.GetFromOtheSilo());
            Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
        }

        /// <summary>
        /// Tests that when a client cannot deserialize a request from a grain, an exception is promptly propagated from the client to the caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_ClientToGrain_DeserializationFailure()
        {
            var obj = new MessageSerializationClientObject();
            var grainFactory = (IInternalGrainFactory)this.fixture.GrainFactory;
            var objRef = grainFactory.CreateObjectReference<IMessageSerializationClientObject>(obj);
            var grain = this.fixture.GrainFactory.GetGrain<IMessageSerializationGrain>(GetRandomGrainId());
            
            var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => grain.SendToClient(objRef));
            Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
        }
        
        /// <summary>
        /// Tests that when a client cannot serialize a response to a grain, an exception is promptly propagated from the client to the caller.
        /// </summary>
        [Fact(Skip = "Flaky. https://github.com/dotnet/orleans/issues/5397"), TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_ClientToGrain_SerializationFailure()
        {
            var obj = new MessageSerializationClientObject();
            var grainFactory = (IInternalGrainFactory)this.fixture.GrainFactory;
            var objRef = grainFactory.CreateObjectReference<IMessageSerializationClientObject>(obj);
            var grain = grainFactory.GetGrain<IMessageSerializationGrain>(GetRandomGrainId());
            
            var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.GetFromClient(objRef));

            // Sometimes the client connection will be terminated (due to the serialization failure which the client caused)
            // before the response can be sent and therefore the response message will be dropped.
            // Ideally we would quickly throw for requests sent to connections which are then terminated before
            // a response is received.
            Assert.True(exception is NotSupportedException || exception is TimeoutException);
            Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
        }

        private class MessageSerializationClientObject : IMessageSerializationClientObject
        {
            public Task Send(UndeserializableType input) => Task.FromResult(input);
            public Task<UnserializableType> Get() => Task.FromResult(new UnserializableType());
        }
    }
}
