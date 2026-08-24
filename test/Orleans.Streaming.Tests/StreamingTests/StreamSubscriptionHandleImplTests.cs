using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.StreamingTests;

public class StreamSubscriptionHandleImplTests
{
    [Fact]
    public void ActiveImplicitSubscriptionRejectsOlderAcknowledgedToken()
    {
        var acknowledgedToken = new EventSequenceTokenV2(10);
        var subscriptionId = CreateSubscriptionId(implicitSubscription: true);

        var exception = Assert.Throws<InvalidOperationException>(
            () => StreamSubscriptionHandleImpl<int>.ValidateResumeToken(
                subscriptionId,
                hasObserver: true,
                StreamHandshakeToken.CreateDeliveyToken(acknowledgedToken),
                new EventSequenceTokenV2(9)));

        Assert.Contains("Implicit subscriptions advance monotonically", exception.Message);
    }

    [Fact]
    public void ActiveImplicitSubscriptionRejectsNewerTokenAfterAcknowledgement()
    {
        var acknowledgedToken = new EventSequenceTokenV2(10);
        var subscriptionId = CreateSubscriptionId(implicitSubscription: true);

        Assert.Throws<InvalidOperationException>(
            () => StreamSubscriptionHandleImpl<int>.ValidateResumeToken(
                subscriptionId,
                hasObserver: true,
                StreamHandshakeToken.CreateDeliveyToken(acknowledgedToken),
                new EventSequenceTokenV2(11)));
    }

    [Fact]
    public void ActiveImplicitSubscriptionAllowsObserverReplacementWithoutToken()
    {
        StreamSubscriptionHandleImpl<int>.ValidateResumeToken(
            CreateSubscriptionId(implicitSubscription: true),
            hasObserver: true,
            StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(10)),
            token: null);
    }

    [Fact]
    public void ActiveExplicitSubscriptionAllowsOlderAcknowledgedToken()
    {
        StreamSubscriptionHandleImpl<int>.ValidateResumeToken(
            CreateSubscriptionId(implicitSubscription: false),
            hasObserver: true,
            StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(10)),
            new EventSequenceTokenV2(9));
    }

    [Fact]
    public void ReconstructedImplicitSubscriptionAllowsRecoveryToken()
    {
        StreamSubscriptionHandleImpl<int>.ValidateResumeToken(
            CreateSubscriptionId(implicitSubscription: true),
            hasObserver: false,
            StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(10)),
            new EventSequenceTokenV2(9));
    }

    private static GuidId CreateSubscriptionId(bool implicitSubscription)
    {
        var subscriptionGuid = implicitSubscription
            ? SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid())
            : SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid());
        return GuidId.GetGuidId(subscriptionGuid);
    }
}
