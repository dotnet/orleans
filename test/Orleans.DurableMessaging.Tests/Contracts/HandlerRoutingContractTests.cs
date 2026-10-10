using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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

        var result = handler.HandleAsync(context, cancellation.Token);
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

        var pending = handler.HandleAsync(context, cancellation.Token).AsTask();
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

        await handler.HandleAsync(context, CancellationToken.None);

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

    [Fact]
    public void HandlerContext_Constructor_RequiresCompletionCallback()
    {
        using var envelope = Envelope();
        var type = typeof(IInboxHandlerContext).Assembly.GetType("Orleans.DurableMessaging.InboxHandlerContext", throwOnError: true)!;
        var exception = Assert.Throws<TargetInvocationException>(() => Activator.CreateInstance(type, [envelope, null]));
        Assert.Equal("complete", Assert.IsType<ArgumentNullException>(exception.InnerException).ParamName);
        var parameters = Assert.Single(type.GetConstructors()).GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(Action), parameters[1].ParameterType);
        Assert.False(parameters[1].IsOptional);
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
