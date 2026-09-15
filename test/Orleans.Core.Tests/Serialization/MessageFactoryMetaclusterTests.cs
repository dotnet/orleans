using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Serialization;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("Serialization")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "5")]
public sealed class MessageFactoryMetaclusterTests(TestEnvironmentFixture environment)
    : IDisposable
{
    [Fact]
    public void CreateRequest_ExportsAllowedRequestContextForRemoteSend()
    {
        RequestContext.Set("tenant", "north");
        RequestContext.Set("attempt", 3);

        var message = GetMessageFactory().CreateMessage("payload", InvokeMethodOptions.ReadOnly);

        Assert.Equal(Message.Directions.Request, message.Direction);
        Assert.Equal("payload", message.BodyObject);
        Assert.Equal("north", message.RequestContextData!["tenant"]);
        Assert.Equal(3, message.RequestContextData["attempt"]);
        Assert.True(message.IsReadOnly);
    }

    [Fact]
    public void CreateRequest_DoesNotLeakExcludedRequestContext()
    {
        RequestContext.Set("tenant", "north");
        RequestContext.ReentrancyId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        using var suppression = RequestContext.SuppressCallChainReentrancy();

        var message = GetMessageFactory().CreateMessage(null, InvokeMethodOptions.None);

        Assert.Equal("north", message.RequestContextData!["tenant"]);
        Assert.DoesNotContain(RequestContext.CALL_CHAIN_REENTRANCY_HEADER, message.RequestContextData);
        Assert.Single(message.RequestContextData);
    }

    [Fact]
    public void CreateOneWayRequest_ExportsSameAllowedContext()
    {
        RequestContext.Set("tenant", "north");
        RequestContext.Set("correlation", 17);

        var request = GetMessageFactory().CreateMessage(null, InvokeMethodOptions.None);
        var oneWay = GetMessageFactory().CreateMessage(null, InvokeMethodOptions.OneWay);

        Assert.Equal(Message.Directions.Request, request.Direction);
        Assert.Equal(Message.Directions.OneWay, oneWay.Direction);
        Assert.Equal(request.RequestContextData, oneWay.RequestContextData);
        Assert.Equal(2, oneWay.RequestContextData!.Count);
    }

    public void Dispose() => RequestContext.Clear();

    private MessageFactory GetMessageFactory() =>
        environment.Services.GetRequiredService<MessageFactory>();
}
