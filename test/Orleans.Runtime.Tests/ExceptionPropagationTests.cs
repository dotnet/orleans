using TestExtensions;

using UnitTests.GrainInterfaces;

using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    /// <summary>
    /// Comprehensive tests for exception propagation across Orleans' distributed system boundaries.
    /// 
    /// Orleans must correctly propagate exceptions from grains back to callers, preserving:
    /// - Original exception types and messages
    /// - Stack traces from the remote grain (critical for debugging distributed systems)
    /// - AggregateException structure (not unwrapped)
    /// - Task cancellation semantics
    /// 
    /// These tests also verify exception propagation during serialization/deserialization failures,
    /// which can occur at multiple points:
    /// - Client -> Grain (request/response)
    /// - Grain -> Client (callbacks)
    /// - Grain -> Grain (cross-silo calls)
    /// 
    /// Proper exception handling is crucial for debugging and maintaining distributed applications.
    /// </summary>
    public class ExceptionPropagationTests : OrleansTestingBase, IClassFixture<ExceptionPropagationTests.Fixture>
    {
        private const int TestIterations = 3;
        private readonly ITestOutputHelper output;
        private readonly Fixture fixture;
        private readonly IMessageSerializationGrain exceptionGrain;
        private readonly MessageSerializationClientObject clientObject = new MessageSerializationClientObject();
        private readonly IMessageSerializationClientObject clientObjectRef;

        public ExceptionPropagationTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;

            var grainFactory = (IInternalGrainFactory)this.fixture.GrainFactory;
            this.exceptionGrain = grainFactory.GetGrain<IMessageSerializationGrain>(GetRandomGrainId());
            this.clientObjectRef = grainFactory.CreateObjectReference<IMessageSerializationClientObject>(this.clientObject);
        }

        public class Fixture : BaseTestClusterFixture
        {
        }

        /// <summary>
        /// Verifies that different exception types thrown by grains are correctly
        /// propagated to the calling client with their original type and message intact.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task ExceptionsPropagatedFromGrainToClient()
        {
            var grain = this.fixture.Client.GetGrain<IExceptionGrain>(0);

            var invalidOperationException = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.ThrowsInvalidOperationException());
            Assert.Equal("Test exception", invalidOperationException.Message);

            var nullReferenceException = await Assert.ThrowsAsync<NullReferenceException>(() => grain.ThrowsNullReferenceException());
            Assert.Equal("null null null", nullReferenceException.Message);
        }

        /// <summary>
        /// Basic test ensuring that exceptions thrown in grain methods
        /// are propagated back to the caller as the same exception type.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task BasicExceptionPropagation()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => grain.ThrowsInvalidOperationException());

            output.WriteLine(exception.ToString());
            Assert.Equal("Test exception", exception.Message);
        }

        /// <summary>
        /// Critical test verifying that remote stack traces are preserved in propagated exceptions.
        /// When debugging distributed systems, it's essential to see where the exception originated
        /// in the remote grain, not just where it was received in the client.
        /// Uses .Wait() to get the raw AggregateException without any async state machine modifications.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task ExceptionContainsOriginalStackTrace()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());

            // Explicitly using .Wait() instead of await the task to avoid any modification of the inner exception
            var originalException = await Task.Run(() => {
                try
                {
#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
                    grain.ThrowsInvalidOperationException().Wait();
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
            var aggEx = Assert.IsType<AggregateException>(originalException);

            var exception = aggEx.InnerException;
            output.WriteLine(exception.ToString());
            Assert.IsAssignableFrom<InvalidOperationException>(exception);
            Assert.Equal("Test exception", exception.Message);
            Assert.Contains("ThrowsInvalidOperationException", exception.StackTrace);
        }

        /// <summary>
        /// Verifies that even when using async/await (which rethrows exceptions),
        /// the original remote stack trace is still preserved in the exception.
        /// This is important for maintaining debuggability in async code.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task ExceptionContainsOriginalStackTraceWhenRethrowingLocally()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            try
            {
                // Use await to force the exception to be rethrown and validate that the remote stack trace is still present
                await grain.ThrowsInvalidOperationException();
                Assert.Fail("should have thrown");
            }
            catch (InvalidOperationException exception)
            {
                output.WriteLine(exception.ToString());
                Assert.IsAssignableFrom<InvalidOperationException>(exception);
                Assert.Equal("Test exception", exception.Message);
                Assert.Contains("ThrowsInvalidOperationException", exception.StackTrace);
            }
        }

        /// <summary>
        /// Ensures that AggregateExceptions thrown by grains are NOT unwrapped during propagation.
        /// This preserves the original exception structure, which may be important for error handling
        /// logic that expects specific exception hierarchies.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task ExceptionPropagationDoesNotUnwrapAggregateExceptions()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => grain.ThrowsAggregateExceptionWrappingInvalidOperationException());

            var nestedEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Test exception", nestedEx.Message);
        }

        /// <summary>
        /// Tests that nested AggregateExceptions maintain their structure during propagation.
        /// Orleans does not flatten the exception hierarchy, preserving the original nesting
        /// that may have semantic meaning in the application.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task ExceptionPropagationDoesNoFlattenAggregateExceptions()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => grain.ThrowsNestedAggregateExceptionsWrappingInvalidOperationException());

            var nestedAggEx = Assert.IsAssignableFrom<AggregateException>(exception.InnerException);
            var doubleNestedEx = Assert.IsAssignableFrom<InvalidOperationException>(nestedAggEx.InnerException);
            Assert.Equal("Test exception", doubleNestedEx.Message);
        }

        /// <summary>
        /// Verifies that task cancellation is properly propagated as TaskCanceledException.
        /// This is important for cooperative cancellation patterns in distributed systems.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task TaskCancelationPropagation()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => grain.Canceled());
        }

        /// <summary>
        /// Tests exception propagation through grain-to-grain calls.
        /// When grain A calls grain B, and grain B throws an exception,
        /// the exception should propagate back through grain A to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task GrainForwardingExceptionPropagation()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var otherGrainId = GetRandomGrainId();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => grain.GrainCallToThrowsInvalidOperationException(otherGrainId));

            Assert.Equal("Test exception", exception.Message);
        }

        [Fact, TestCategory("BVT")]
        public async Task GrainForwardingExceptionPropagationDoesNotUnwrapAggregateExceptions()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var otherGrainId = GetRandomGrainId();
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => grain.GrainCallToThrowsAggregateExceptionWrappingInvalidOperationException(otherGrainId));

            var nestedEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Test exception", nestedEx.Message);
        }

        /// <summary>
        /// Ensures that exceptions thrown synchronously in grain methods (before any await)
        /// still result in a faulted Task rather than throwing synchronously to the caller.
        /// This maintains consistent async behavior regardless of where exceptions occur.
        /// </summary>
        [Fact, TestCategory("BVT")]
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

        /// <summary>
        /// Tests that when a Task is faulted with multiple exceptions, all exceptions
        /// are propagated in the AggregateException, not just the first one.
        /// This is currently skipped pending implementation of issue #1378.
        /// </summary>
        [Fact(Skip = "Implementation of issue #1378 is still pending"), TestCategory("BVT")]
        public async Task ExceptionPropagationForwardsEntireAggregateException()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var grainCall = grain.ThrowsMultipleExceptionsAggregatedInFaultedTask();

            var originalException = await Task.Run(() =>
            {
                try
                {
#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
                    // use Wait() so that we get the entire AggregateException ('await' would just catch the first inner exception)
                    // Do not use Assert.Throws to avoid any tampering of the AggregateException itself from the test framework
                    grainCall.Wait();
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
            var exception = Assert.IsType<AggregateException>(originalException);

            output.WriteLine(exception.ToString());

            // make sure that all exceptions in the task are present, and not just the first one.
            Assert.Equal(2, exception.InnerExceptions.Count);
            var firstEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerExceptions[0]);
            Assert.Equal("Test exception 1", firstEx.Message);
            var secondEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerExceptions[1]);
            Assert.Equal("Test exception 2", secondEx.Message);
        }

        [Fact, TestCategory("BVT")]
        public async Task SynchronousAggregateExceptionThrownShouldResultInFaultedTaskWithOriginalAggregateExceptionUnmodifiedAsInnerException()
        {
            IExceptionGrain grain = this.fixture.GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());

            // start the grain call but don't await it nor wrap in try/catch, to make sure it doesn't throw synchronously
            var grainCallTask = grain.ThrowsSynchronousAggregateExceptionWithMultipleInnerExceptions();

            // assert that the faulted task has an inner exception of type AggregateException, which should be our original exception
            var exception = await Assert.ThrowsAsync<AggregateException>(() => grainCallTask);

            Assert.StartsWith("Test AggregateException message", exception.Message);

            // make sure that all exceptions in the task are present, and not just the first one.
            Assert.Equal(2, exception.InnerExceptions.Count);
            var firstEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerExceptions[0]);
            Assert.Equal("Test exception 1", firstEx.Message);
            var secondEx = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerExceptions[1]);
            Assert.Equal("Test exception 2", secondEx.Message);
        }

        /// <summary>
        /// Tests exception propagation when a client cannot deserialize a request from a grain.
        /// This scenario occurs when a grain makes a callback to a client observer, but the
        /// request contains types that cannot be deserialized on the client side.
        /// The serialization failure should be detected and propagated back to the grain.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainCallsClient_Request_Deserialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => exceptionGrain.SendUndeserializableToClient(this.clientObjectRef));
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests that when a client cannot serialize a response to a grain, an exception is promptly propagated back to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainCallsClient_Response_Serialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => exceptionGrain.GetUnserializableFromClient(this.clientObjectRef));
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests that when a grain cannot deserialize a response from a client, an exception is promptly propagated back to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainCallsClient_Response_Deserialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => exceptionGrain.GetUndeserializableFromClient(this.clientObjectRef));
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests that when a grain cannot serialize a request to a client, an exception is promptly propagated back to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainCallsClient_Request_Serialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => exceptionGrain.SendUnserializableToClient(this.clientObjectRef));
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests exception propagation for grain-to-grain calls when deserialization fails.
        /// This can happen in cross-silo scenarios where grains have incompatible type versions
        /// or when using custom serializers that fail. The exception should propagate back
        /// through the call chain to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainCallsGrain_Request_Deserialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => exceptionGrain.SendUndeserializableToOtherSilo());
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests that when a grain cannot serialize a request to another grain, an exception is promptly propagated back to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainCallsGrain_Request_Serialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => exceptionGrain.SendUnserializableToOtherSilo());
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests that when a grain cannot serialize a response to another grain, an exception is promptly propagated back to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainCallsGrain_Response_Serialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => exceptionGrain.GetUnserializableFromOtherSilo());
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests that when a grain cannot deserialize a response from another grain, an exception is promptly propagated back to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_GrainCallsGrain_Response_Deserialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => exceptionGrain.GetUndeserializableFromOtherSilo());
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests the most common serialization failure scenario: client sends a request
        /// that the grain cannot deserialize. This validates that Orleans properly detects
        /// the failure and sends an appropriate exception back to the client rather than
        /// silently failing or hanging.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_ClientCallsGrain_Request_Deserialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => exceptionGrain.SendUndeserializable(new UndeserializableType(32)));
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests that when a client cannot serialize a request to a grain, an exception is promptly propagated back to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_ClientCallsGrain_Request_Serialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<NotSupportedException>(() => exceptionGrain.SendUnserializable(new UnserializableType()));
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests that when a grain cannot serialize a response to a client, an exception is promptly propagated back to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_ClientCallsGrain_Response_Serialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<Exception>(() => exceptionGrain.GetUnserializable());
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        /// <summary>
        /// Tests that when a client cannot deserialize a response from a grain, an exception is promptly propagated back to the original caller.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Messaging"), TestCategory("Serialization")]
        public async Task ExceptionPropagation_ClientCallsGrain_Response_Deserialization_Failure()
        {
            for (var i = 0; i < TestIterations; i++)
            {
                var exception = await Assert.ThrowsAnyAsync<Exception>(() => exceptionGrain.GetUndeserializable());
                Assert.Contains(UndeserializableType.FailureMessage, exception.Message);
            }
        }

        private class MessageSerializationClientObject : IMessageSerializationClientObject
        {
            public Task SendUndeserializable(UndeserializableType input) => Task.FromResult(input);
            public Task SendUnserializable(UnserializableType input) => Task.FromResult(input);
            public Task<UnserializableType> GetUnserializable() => Task.FromResult(new UnserializableType());
            public Task<UndeserializableType> GetUndeserializable() => Task.FromResult(new UndeserializableType(35));
        }
    }
}
