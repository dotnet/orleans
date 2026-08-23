using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization.Invocation;
using TestExtensions;
using Xunit;

namespace UnitTests.Messaging;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("Serialization")]
public class InvokableMessageOwnershipTests
{
    private readonly MessageFactory _messageFactory;
    private readonly MessageSerializer _messageSerializer;

    public InvokableMessageOwnershipTests(TestEnvironmentFixture fixture)
    {
        _messageFactory = fixture.Services.GetRequiredService<MessageFactory>();
        _messageSerializer = fixture.Services.GetRequiredService<MessageSerializer>();
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RequestMessageOwnsIndependentRequestCopy()
    {
        var request = new TestInvokableRequest { Value = 42 };

        var message = _messageFactory.CreateMessage(request, InvokeMethodOptions.None);
        var copy = Assert.IsType<TestInvokableRequest>(message.BodyObject);

        Assert.NotSame(request, copy);
        Assert.Equal(request.Value, copy.Value);
        Assert.True(message.OwnsBodyObject);

        message.DisposeOwnedBody();

        Assert.Equal(0, request.DisposeCount);
        Assert.Equal(1, copy.DisposeCount);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void OneWayMessageOwnsIndependentRequestCopy()
    {
        var request = new TestInvokableRequest { Value = 42 };

        var message = _messageFactory.CreateMessage(request, InvokeMethodOptions.OneWay);
        var copy = Assert.IsType<TestInvokableRequest>(message.BodyObject);

        Assert.NotSame(request, copy);
        Assert.Equal(request.Value, copy.Value);
        Assert.True(message.OwnsBodyObject);

        message.DisposeOwnedBody();
        message.DisposeOwnedBody();

        Assert.Equal(0, request.DisposeCount);
        Assert.Equal(1, copy.DisposeCount);
        Assert.Null(message.BodyObject);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void DeserializedRequestMessageOwnsRequest()
    {
        var request = new TestInvokableRequest { Value = 42 };
        var message = _messageFactory.CreateMessage(request, InvokeMethodOptions.None);

        var deserializedMessage = RoundTrip(message);
        var deserializedRequest = Assert.IsType<TestInvokableRequest>(deserializedMessage.BodyObject);

        Assert.NotSame(request, deserializedRequest);
        Assert.Equal(request.Value, deserializedRequest.Value);
        Assert.True(deserializedMessage.OwnsBodyObject);

        deserializedMessage.DisposeOwnedBody();

        Assert.Equal(0, request.DisposeCount);
        Assert.Equal(1, deserializedRequest.DisposeCount);
    }

    private Message RoundTrip(Message message)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        _messageSerializer.Write(pipe.Writer, message);
        pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();

        pipe.Reader.TryRead(out var readResult);
        var reader = readResult.Buffer;
        var (requiredBytes, _, _) = _messageSerializer.TryRead(ref reader, out var deserializedMessage);
        Assert.Equal(0, requiredBytes);
        return deserializedMessage!;
    }
}

[GenerateSerializer]
internal sealed class TestInvokableRequest : RequestBase
{
    [Id(0)]
    public int Value { get; set; }

    [field: NonSerialized]
    public int DisposeCount { get; private set; }

    public override object GetTarget() => null!;

    public override void SetTarget(ITargetHolder holder) { }

    public override ValueTask<Response> Invoke() => new(Response.Completed);

    public override string GetMethodName() => nameof(TestInvokableRequest);

    public override string GetInterfaceName() => nameof(TestInvokableRequest);

    public override string GetActivityName() => nameof(TestInvokableRequest);

    public override MethodInfo GetMethod() => typeof(TestInvokableRequest).GetMethod(nameof(Invoke))!;

    public override Type GetInterfaceType() => typeof(TestInvokableRequest);

    public override void Dispose() => DisposeCount++;
}
