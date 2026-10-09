using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class HandlerRoutingContractTests
{
    [Fact]
    public void RegisterHandler_FirstRegistration_PreservesHandlerIdentity()
    {
        var inbox = CreateInbox(out var storage);
        Assert.Null(RegisteredHandler(inbox));
        var handler = Substitute.For<IInboxHandler>();

        inbox.RegisterHandler(handler);

        Assert.Same(handler, RegisteredHandler(inbox));
        Assert.Equal(1000, inbox.Capacity);
        storage.DidNotReceiveWithAnyArgs().TryGetValue(default, out _);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisterHandler_SecondRegistration_RejectsWithoutReplacingOriginal(bool sameInstance)
    {
        var inbox = CreateInbox(out _);
        var first = Substitute.For<IInboxHandler>();
        inbox.RegisterHandler(first);
        var replacement = sameInstance ? first : Substitute.For<IInboxHandler>();

        var exception = Assert.Throws<InvalidOperationException>(() => inbox.RegisterHandler(replacement));

        Assert.Equal("A handler is already registered for this durable inbox.", exception.Message);
        Assert.Same(first, RegisteredHandler(inbox));
        Assert.Equal(1000, inbox.Capacity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisterHandler_NullRegistration_RejectsWithoutChangingHandler(bool alreadyRegistered)
    {
        var inbox = CreateInbox(out _);
        var handler = Substitute.For<IInboxHandler>();
        if (alreadyRegistered) inbox.RegisterHandler(handler);

        var exception = Assert.Throws<ArgumentNullException>(() => inbox.RegisterHandler(null!));

        Assert.Equal("handler", exception.ParamName);
        if (alreadyRegistered)
        {
            Assert.Same(handler, RegisteredHandler(inbox));
        }
        else
        {
            Assert.Null(RegisteredHandler(inbox));
            inbox.RegisterHandler(handler);
            Assert.Same(handler, RegisteredHandler(inbox));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HandlerContext_Complete_ForwardsToOwnerSynchronously(int calls)
    {
        var count = 0;
        using var envelope = Envelope();
        var context = CreateContext(envelope, () => count++);

        for (var i = 0; i < calls; i++)
        {
            context.Complete();
            Assert.Equal(i + 1, count);
        }

        Assert.Equal(calls, count);
        AssertEnvelope(envelope, context.Envelope);
    }

    [Fact]
    public void HandlerContext_Complete_PropagatesOriginalCallbackFailureWithoutChangingEnvelope()
    {
        var sentinel = new InvalidOperationException("owner failure");
        var count = 0;
        using var envelope = Envelope();
        var context = CreateContext(envelope, () =>
        {
            count++;
            throw sentinel;
        });

        var exception = Assert.Throws<InvalidOperationException>(context.Complete);

        Assert.Same(sentinel, exception);
        Assert.Equal(1, count);
        AssertEnvelope(envelope, context.Envelope);
    }

    [Fact]
    public void HandlerContext_Envelope_PreservesIdentityAndPayload()
    {
        byte[] source = [0x00, 0xff, 0x80];
        using var writer = new ArcBufferWriter();
        writer.Write(source);
        using var envelope = Envelope(writer.PeekSlice(writer.Length));
        var count = 0;
        var context = CreateContext(envelope, () => count++);
        Array.Fill(source, (byte)0x42);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();

        using var decoded = services.GetRequiredService<Serializer<DurableEnvelope>>()
            .Deserialize(services.GetRequiredService<Serializer<DurableEnvelope>>().SerializeToArray(context.Envelope));
        context.Complete();

        Assert.Equal(1, count);
        AssertEnvelope(envelope, context.Envelope);
        Assert.Equal(envelope.MessageId, decoded.MessageId);
        Assert.Equal(envelope.Subject, decoded.Subject);
        Assert.Equal(envelope.SenderId, decoded.SenderId);
        Assert.Equal(envelope.ReceiverId, decoded.ReceiverId);
        Assert.Equal(new byte[] { 0x00, 0xff, 0x80 }, decoded.Payload.ToArray());
        Assert.Equal(new byte[] { 0x00, 0xff, 0x80 }, context.Envelope.Payload.ToArray());
    }

    [Fact]
    public async Task HandleAsync_SynchronousHandling_PreservesContextTokenAndExplicitCompletion()
    {
        var count = 0;
        using var envelope = Envelope();
        var context = CreateContext(envelope, () => count++);
        using var cancellation = new CancellationTokenSource();
        var handler = Substitute.For<IInboxHandler>();
        handler.HandleAsync(context, cancellation.Token).Returns(_ =>
        {
            context.Complete();
            return ValueTask.CompletedTask;
        });
        var inbox = CreateInbox(out _);
        inbox.RegisterHandler(handler);

        var result = RegisteredHandler(inbox)!.HandleAsync(context, cancellation.Token);
        Assert.True(result.IsCompletedSuccessfully);
        await result;

        Assert.Equal(1, count);
        AssertEnvelope(envelope, context.Envelope);
        await handler.Received(1).HandleAsync(context, cancellation.Token);
    }

    [Fact]
    public async Task HandleAsync_DeferredWork_PreservesContextAndCompletesOnlyAfterRelease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var envelope = Envelope();
        var context = CreateContext(envelope, () => count++);
        using var cancellation = new CancellationTokenSource();
        var handler = Substitute.For<IInboxHandler>();
        async ValueTask Handle()
        {
            entered.SetResult();
            await release.Task;
            context.Complete();
        }
        handler.HandleAsync(context, cancellation.Token).Returns(_ => Handle());
        var inbox = CreateInbox(out _);
        inbox.RegisterHandler(handler);

        var pending = RegisteredHandler(inbox)!.HandleAsync(context, cancellation.Token).AsTask();
        await entered.Task;
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, count);
        release.SetResult();
        await pending;

        Assert.Equal(1, count);
        AssertEnvelope(envelope, context.Envelope);
        await handler.Received(1).HandleAsync(context, cancellation.Token);
    }

    [Fact]
    public async Task HandleAsync_ReturnWithoutComplete_DoesNotInvokeCompletion()
    {
        var count = 0;
        using var envelope = Envelope();
        var context = CreateContext(envelope, () => count++);
        var handler = Substitute.For<IInboxHandler>();
        handler.HandleAsync(context, CancellationToken.None).Returns(ValueTask.CompletedTask);
        var inbox = CreateInbox(out _);
        inbox.RegisterHandler(handler);

        await RegisteredHandler(inbox)!.HandleAsync(context, CancellationToken.None);

        Assert.Equal(0, count);
        AssertEnvelope(envelope, context.Envelope);
        await handler.Received(1).HandleAsync(context, CancellationToken.None);
    }

    [Fact]
    public void HandlerNotFound_RoundTripsStatusAndDiagnostic()
    {
        var result = DeliveryResult.HandlerNotFound();
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<DeliveryResult>>();

        var decoded = serializer.Deserialize(serializer.SerializeToArray(result));

        Assert.Equal(DeliveryStatus.HandlerNotFound, result.Status);
        Assert.Equal(DeliveryStatus.HandlerNotFound, decoded.Status);
        Assert.Equal("No inbox handler is registered.", result.Message);
        Assert.Equal("No inbox handler is registered.", decoded.Message);
    }

    [Fact]
    public void ExternalConsumerAssembly_HasNoFriendAccessToDurableMessaging()
    {
        var consumer = typeof(HandlerRoutingContractTests).Assembly.GetName().Name!;
        Assert.DoesNotContain(typeof(IInboxHandler).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>(),
            attribute => attribute.AssemblyName.Split(',')[0] == consumer);
    }

    private static IDurableInbox CreateInbox(out IDurableDictionary<HierarchicalKey, DurableEnvelope> storage)
    {
        var type = typeof(IDurableInbox).Assembly.GetType("Orleans.DurableMessaging.DurableInbox", throwOnError: true)!;
        storage = Substitute.For<IDurableDictionary<HierarchicalKey, DurableEnvelope>>();
        return Assert.IsAssignableFrom<IDurableInbox>(Activator.CreateInstance(type, storage, 1000));
    }

    private static IInboxHandler? RegisteredHandler(IDurableInbox inbox)
    {
        var method = inbox.GetType().GetMethod("TryGetHandler", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object?[] arguments = [null];
        var found = Assert.IsType<bool>(method.Invoke(inbox, arguments));
        if (!found)
        {
            Assert.Null(arguments[0]);
            return null;
        }
        return Assert.IsAssignableFrom<IInboxHandler>(arguments[0]);
    }

    private static IInboxHandlerContext CreateContext(DurableEnvelope envelope, Action complete)
    {
        var type = typeof(IInboxHandlerContext).Assembly.GetType("Orleans.DurableMessaging.InboxHandlerContext", throwOnError: true)!;
        return Assert.IsAssignableFrom<IInboxHandlerContext>(Activator.CreateInstance(type, envelope, complete));
    }

    private static DurableEnvelope Envelope(ArcBuffer? payload = null)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 0x00, 0xff, 0x80 });
        return new()
        {
            MessageId = HierarchicalKey.Create("44444444-4444-4444-4444-444444444444"),
            SenderId = GrainId.Create("sender", "handler"),
            ReceiverId = GrainId.Create("receiver", "handler"),
            Subject = "handler.v1",
            Payload = payload ?? writer.PeekSlice(writer.Length)
        };
    }

    private static void AssertEnvelope(DurableEnvelope expected, DurableEnvelope actual)
    {
        Assert.Equal(expected.MessageId, actual.MessageId);
        Assert.Equal(expected.Subject, actual.Subject);
        Assert.Equal(expected.SenderId, actual.SenderId);
        Assert.Equal(expected.ReceiverId, actual.ReceiverId);
        Assert.Same(expected.Payload.First, actual.Payload.First);
        Assert.Equal(new byte[] { 0x00, 0xff, 0x80 }, actual.Payload.ToArray());
    }
}
