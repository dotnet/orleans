using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using TestExtensions;
using Xunit;

namespace UnitTests.Messaging
{
    /// <summary>
    /// Tests for Message pooling and ownership tracking.
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class MessagePoolTests
    {
        private readonly MessageFactory _messageFactory;

        public MessagePoolTests(TestEnvironmentFixture fixture)
        {
            _messageFactory = fixture.Services.GetRequiredService<MessageFactory>();
        }

        [Fact, TestCategory("BVT"), TestCategory("Messaging")]
        public void Message_RefCount_InitializedToOne()
        {
            var message = MessagePool.Get();

            Assert.NotNull(message);

            message.Release();
        }

        [Fact, TestCategory("BVT"), TestCategory("Messaging")]
        public void Message_Acquire_IncrementsRefCount()
        {
            var message = MessagePool.Get();

            message.Acquire();

            message.Release();
            message.Release();
        }

        [Fact, TestCategory("BVT"), TestCategory("Messaging")]
        public void Message_ReleaseDropped_ReleasesMessage()
        {
            var message = MessagePool.Get();

            message.ReleaseDropped("TestReason");
        }

        [Fact, TestCategory("BVT"), TestCategory("Messaging")]
        public void Message_MultipleAcquireRelease_WorksCorrectly()
        {
            var message = MessagePool.Get();

            message.Acquire();
            message.Acquire();

            message.Release();
            message.Release();
            message.Release();
        }

        [Fact, TestCategory("BVT"), TestCategory("Messaging")]
        public void MessageFactory_CreateMessage_ReturnsPooledMessage()
        {
            var message = _messageFactory.CreateMessage(null, InvokeMethodOptions.None);

            Assert.NotNull(message);
            Assert.Equal(Message.Directions.Request, message.Direction);

            message.Release();
        }

        [Fact, TestCategory("BVT"), TestCategory("Messaging")]
        public void Message_MarkTransferred_DoesNotThrow()
        {
            var message = MessagePool.Get();

            message.MarkTransferred("TestTransfer");
            message.MarkTransferred("AnotherTransfer");

            message.Release();
        }

#if DEBUG
        [Fact, TestCategory("BVT"), TestCategory("Messaging")]
        public void MessagePool_LeakTracking_TracksOutstandingMessages()
        {
            MessagePool.ClearLeakTracking();
            MessagePool.EnableLeakTracking = true;

            try
            {
                var message1 = MessagePool.Get();
                var message2 = MessagePool.Get();

                var outstanding = MessagePool.GetOutstandingMessages();
                Assert.Equal(2, outstanding.Count);

                message1.Release();
                outstanding = MessagePool.GetOutstandingMessages();
                Assert.Single(outstanding);

                message2.Release();
                outstanding = MessagePool.GetOutstandingMessages();
                Assert.Empty(outstanding);
            }
            finally
            {
                MessagePool.EnableLeakTracking = false;
                MessagePool.ClearLeakTracking();
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Messaging")]
        public void MessagePool_LeakTracking_CapturesAllocationInfo()
        {
            MessagePool.ClearLeakTracking();
            MessagePool.EnableLeakTracking = true;

            try
            {
                var message = MessagePool.Get();

                var outstanding = MessagePool.GetOutstandingMessages();
                Assert.Single(outstanding);

                var info = outstanding.First();

                Assert.Same(message, info.Message);
                Assert.NotNull(info.AllocationStack);
                Assert.True(info.AllocationTime <= DateTime.UtcNow);

                message.Release();
            }
            finally
            {
                MessagePool.EnableLeakTracking = false;
                MessagePool.ClearLeakTracking();
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Messaging")]
        public void MessagePool_LeakTracking_DisabledByDefault()
        {
            MessagePool.EnableLeakTracking = false;
            MessagePool.ClearLeakTracking();

            var message = MessagePool.Get();

            var outstanding = MessagePool.GetOutstandingMessages();
            Assert.Empty(outstanding);

            message.Release();
        }
#endif

        [Fact, TestCategory("BVT"), TestCategory("Messaging")]
        public void Message_Reset_ClearsAllFields()
        {
            var message = MessagePool.Get();
            message.Direction = Message.Directions.Request;
            message.TargetGrain = GrainId.Create("test", "key");
            message.SendingGrain = GrainId.Create("sender", "key");
            message.BodyObject = "test body";

            message.Release();

            var newMessage = MessagePool.Get();

            Assert.Equal(Message.Directions.None, newMessage.Direction);
            Assert.True(newMessage.TargetGrain.IsDefault);
            Assert.True(newMessage.SendingGrain.IsDefault);
            Assert.Null(newMessage.BodyObject);

            newMessage.Release();
        }
    }
}
