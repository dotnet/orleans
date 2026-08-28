using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using TestExtensions;
using Xunit;

namespace UnitTests.Messaging;

[CollectionDefinition(MessagePoolTestCollection.Name, DisableParallelization = true)]
public sealed class MessagePoolTestCollection : ICollectionFixture<TestEnvironmentFixture>
{
    public const string Name = "MessagePoolTests";
}

/// <summary>
/// Tests for Message pooling and ownership tracking.
/// </summary>
[Collection(MessagePoolTestCollection.Name)]
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
        MessagePool.ClearCurrentThreadPool();
        var message = MessagePool.Get();
        var state = message.StateIdentity;

        Assert.Equal(0, MessagePool.GetCachedMessageCount());

        message.Release();

        Assert.Equal(1, MessagePool.GetCachedMessageCount());
        var reused = MessagePool.Get();
        Assert.Same(state, reused.StateIdentity);
        Assert.Equal(0, MessagePool.GetCachedMessageCount());
        reused.Release();
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

            Assert.Equal(message, info.Message);
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
        MessagePool.ClearCurrentThreadPool();
        var message = MessagePool.Get();
        var state = message.StateIdentity;
        message.Direction = Message.Directions.Request;
        message.Result = Message.ResponseTypes.Error;
        message.RetryCount = 3;
        message.ForwardCount = 4;
        message.Id = CorrelationId.GetNext();
        message.IsSystemMessage = true;
        message.IsReadOnly = true;
        message.IsAlwaysInterleave = true;
        message.IsUnordered = true;
        message.IsLocalOnly = true;
        message.IsKeepAlive = false;
        message.TimeToLive = TimeSpan.FromSeconds(30);
        message.TargetSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
        message.TargetGrain = GrainId.Create("test", "key");
        message.SendingSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11112), 2);
        message.SendingGrain = GrainId.Create("sender", "key");
        message.InterfaceType = GrainInterfaceType.Create("test.interface");
        message.InterfaceVersion = 7;
        message.BodyObject = "test body";
        message.RequestContextData = new Dictionary<string, object> { ["key"] = "value" };
        var invalidAddress = GrainAddress.NewActivationAddress(message.TargetSilo, message.TargetGrain);
        message.CacheInvalidationHeader = [new GrainAddressCacheUpdate(invalidAddress, validAddress: null)];

        message.Release();

        var newMessage = MessagePool.Get();

        Assert.Same(state, newMessage.StateIdentity);
        Assert.Equal(Message.Directions.None, newMessage.Direction);
        Assert.Equal(Message.ResponseTypes.None, newMessage.Result);
        Assert.Equal(0, newMessage.RetryCount);
        Assert.Equal(0, newMessage.ForwardCount);
        Assert.Equal(default, newMessage.Id);
        Assert.False(newMessage.IsSystemMessage);
        Assert.False(newMessage.IsReadOnly);
        Assert.False(newMessage.IsAlwaysInterleave);
        Assert.False(newMessage.IsUnordered);
        Assert.False(newMessage.IsLocalOnly);
        Assert.True(newMessage.IsKeepAlive);
        Assert.Null(newMessage.TimeToLive);
        Assert.Null(newMessage.TargetSilo);
        Assert.True(newMessage.TargetGrain.IsDefault);
        Assert.Null(newMessage.SendingSilo);
        Assert.True(newMessage.SendingGrain.IsDefault);
        Assert.True(newMessage.InterfaceType.IsDefault);
        Assert.Equal(0, newMessage.InterfaceVersion);
        Assert.Null(newMessage.BodyObject);
        Assert.Null(newMessage.RequestContextData);
        Assert.Null(newMessage.CacheInvalidationHeader);

        newMessage.Release();
    }

    [Fact, TestCategory("BVT"), TestCategory("Messaging")]
    public void Message_DoubleReleaseThrows()
    {
        MessagePool.ClearCurrentThreadPool();
        var message = MessagePool.Get();
        message.Release();

        Assert.Throws<InvalidOperationException>(() => message.Release());
    }

    [Fact, TestCategory("BVT"), TestCategory("Messaging")]
    public void Message_StaleHandleCannotAffectReusedState()
    {
        MessagePool.ClearCurrentThreadPool();
        var stale = MessagePool.Get();
        var state = stale.StateIdentity;
        stale.BodyObject = "stale";
        stale.Release();

        var current = MessagePool.Get();
        Assert.Same(state, current.StateIdentity);
        current.BodyObject = "current";

        Assert.Throws<InvalidOperationException>(() => stale.BodyObject = "corrupt");
        Assert.Throws<InvalidOperationException>(() => stale.Release());
        Assert.Equal("current", current.BodyObject);

        current.Release();
    }

    [Fact, TestCategory("BVT"), TestCategory("Messaging")]
    public async Task Message_StaleHandleCannotAffectStateReusedOnAnotherThread()
    {
        var stale = MessagePool.Get();
        var state = stale.StateIdentity;
        var cancellationToken = TestContext.Current.CancellationToken;
        using var releaseCurrent = new ManualResetEventSlim();
        var currentSource = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new Thread(() =>
        {
            stale.Release();
            var current = MessagePool.Get();
            current.BodyObject = "current";
            currentSource.SetResult(current);
            releaseCurrent.Wait(cancellationToken);
            current.Release();
        });
        consumer.Start();

        var current = await currentSource.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        try
        {
            Assert.Same(state, current.StateIdentity);
            Assert.Throws<InvalidOperationException>(() => stale.BodyObject = "corrupt");
            Assert.Throws<InvalidOperationException>(() => stale.Release());
            Assert.Equal("current", current.BodyObject);
        }
        finally
        {
            releaseCurrent.Set();
            consumer.Join();
        }
    }

#if DEBUG
    [Fact, TestCategory("BVT"), TestCategory("Messaging")]
    public void MessagePool_CrossThreadReleaseLeavesNoOutstandingOwnership()
    {
        MessagePool.ClearLeakTracking();
        MessagePool.EnableLeakTracking = true;
        using var queue = new BlockingCollection<Message>(boundedCapacity: 32);
        var consumer = new Thread(() =>
        {
            foreach (var message in queue.GetConsumingEnumerable())
            {
                message.Release();
            }
        });

        try
        {
            consumer.Start();
            for (var i = 0; i < 256; i++)
            {
                queue.Add(MessagePool.Get(), TestContext.Current.CancellationToken);
            }

            queue.CompleteAdding();
            consumer.Join();
            Assert.Empty(MessagePool.GetOutstandingMessages());
        }
        finally
        {
            queue.CompleteAdding();
            if (consumer.IsAlive)
            {
                consumer.Join();
            }

            MessagePool.EnableLeakTracking = false;
            MessagePool.ClearLeakTracking();
        }
    }
#endif

    [Fact, TestCategory("BVT"), TestCategory("Messaging")]
    public void Message_ConcurrentOwnersReturnStateOnce()
    {
        MessagePool.ClearCurrentThreadPool();
        var message = MessagePool.Get();

        Parallel.For(0, 10_000, _ =>
        {
            message.Acquire();
            message.Release();
        });

        message.Release();
        Assert.Equal(1, MessagePool.GetCachedMessageCount());
    }

    [Fact, TestCategory("BVT"), TestCategory("Messaging")]
    public async Task Message_FinalReleaseWaitsForActiveMutation()
    {
        MessagePool.ClearCurrentThreadPool();
        var stale = MessagePool.Get();
        var state = Assert.IsType<Message.MessageState>(stale.StateIdentity);
        state.EnterMutation(stale.GenerationForTesting);

        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTask = Task.Run(() =>
        {
            releaseStarted.SetResult();
            stale.Release();
        }, TestContext.Current.CancellationToken);

        try
        {
            await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(() => state.RefCountForTesting == 0, TimeSpan.FromSeconds(5)));
            Assert.False(releaseTask.IsCompleted);
        }
        finally
        {
            state.ExitMutation();
        }

        await releaseTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var current = MessagePool.Get();
        Assert.Same(state, current.StateIdentity);
        current.BodyObject = "current";

        Assert.Throws<InvalidOperationException>(() => stale.BodyObject = "corrupt");
        Assert.Throws<InvalidOperationException>(() => stale.Release());
        Assert.Equal("current", current.BodyObject);

        current.Release();
    }
}
