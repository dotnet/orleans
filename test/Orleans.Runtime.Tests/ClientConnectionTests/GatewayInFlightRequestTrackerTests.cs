using System.Net;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using TestExtensions;
using Xunit;

namespace Tester;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class GatewayInFlightRequestTrackerTests
{
    private static readonly SiloAddress Silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
    private static readonly SiloAddress Silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 22222), 2);

    [Fact]
    public void OneWayMessagesAreNotTracked()
    {
        var tracker = CreateTracker();

        var tracked = tracker.Track(CreateMessage(1, Message.Directions.OneWay, Silo1));

        Assert.False(tracked);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void RequestsWithoutFinalDestinationAreNotTracked()
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        request.TargetSilo = null;

        var tracked = tracker.Track(request);

        Assert.False(tracked);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void SystemTargetRequestsAreNotTracked()
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        request.TargetGrain = SystemTargetGrainId.Create(Constants.CatalogType, Silo1).GrainId;

        var tracked = tracker.Track(request);

        Assert.False(tracked);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void StatusResponseRetainsRequest()
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        Assert.True(tracker.Track(request));

        var completed = tracker.TryComplete(CreateResponse(request, Message.ResponseTypes.Status));

        Assert.False(completed);
        Assert.Equal(1, tracker.Count);
    }

    [Theory]
    [InlineData((int)Message.ResponseTypes.Success)]
    [InlineData((int)Message.ResponseTypes.Error)]
    [InlineData((int)Message.ResponseTypes.Rejection)]
    public void TerminalResponseRemovesRequest(int responseType)
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        Assert.True(tracker.Track(request));

        var completed = tracker.TryComplete(CreateResponse(request, (Message.ResponseTypes)responseType));

        Assert.True(completed);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void NonResponseMessageDoesNotCompleteRequest()
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        Assert.True(tracker.Track(request));

        var completed = tracker.TryComplete(request);

        Assert.False(completed);
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void RemoveForSiloRemovesOnlyRequestsForThatDestination()
    {
        var tracker = CreateTracker();
        var request1 = CreateMessage(1, Message.Directions.Request, Silo1);
        var request2 = CreateMessage(2, Message.Directions.Request, Silo2);
        var request3 = CreateMessage(3, Message.Directions.Request, Silo1);
        Assert.True(tracker.Track(request1));
        Assert.True(tracker.Track(request2));
        Assert.True(tracker.Track(request3));

        var removed = tracker.RemoveForSilo(Silo1);

        Assert.NotNull(removed);
        Assert.Equal(2, removed.Count);
        Assert.Contains(removed, message => message.Id == request1.Id && Silo1.Equals(message.TargetSilo));
        Assert.Contains(removed, message => message.Id == request3.Id && Silo1.Equals(message.TargetSilo));
        Assert.Equal(1, tracker.Count);
        Assert.True(tracker.TryComplete(CreateResponse(request2, Message.ResponseTypes.Success)));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void DuplicateRequestTrackingUsesLatestDestination()
    {
        var tracker = CreateTracker();
        var firstAttempt = CreateMessage(1, Message.Directions.Request, Silo1);
        var retry = CreateMessage(1, Message.Directions.Request, Silo2);
        Assert.True(tracker.Track(firstAttempt));

        Assert.True(tracker.Track(retry));

        Assert.Equal(1, tracker.Count);
        Assert.Null(tracker.RemoveForSilo(Silo1));
        var removed = Assert.Single(tracker.RemoveForSilo(Silo2)!);
        Assert.Equal(retry.Id, removed.Id);
        Assert.Equal(Silo2, removed.TargetSilo);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void RemovingOldDestinationDoesNotStealSameIdRetry()
    {
        var tracker = CreateTracker();
        var original = CreateMessage(1, Message.Directions.Request, Silo1);
        var retry = CreateMessage(1, Message.Directions.Request, Silo2);
        Assert.True(tracker.Track(original));
        Assert.True(tracker.Track(retry));

        Assert.False(tracker.TryRemove(original.Id, Silo1, out _));
        Assert.Equal(1, tracker.Count);
        Assert.True(tracker.TryRemove(retry.Id, Silo2, out var removed));
        Assert.Equal(Silo2, removed.TargetSilo);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void ClearRemovesAllRequestsOnDisconnect()
    {
        var tracker = CreateTracker();
        var request1 = CreateMessage(1, Message.Directions.Request, Silo1);
        var request2 = CreateMessage(2, Message.Directions.Request, Silo2);
        Assert.True(tracker.Track(request1));
        Assert.True(tracker.Track(request2));

        tracker.Clear();

        Assert.Equal(0, tracker.Count);
        Assert.False(tracker.TryComplete(CreateResponse(request1, Message.ResponseTypes.Success)));
        Assert.False(tracker.TryComplete(CreateResponse(request2, Message.ResponseTypes.Error)));
    }

    [Fact]
    public void SnapshotDoesNotRetainBodyOrMutableCacheHeader()
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        request.BodyObject = new object();
        request.CacheInvalidationHeader = [];
        Assert.True(tracker.Track(request));

        request.CacheInvalidationHeader.Add(new GrainAddressCacheUpdate(
            new GrainAddress
            {
                GrainId = request.TargetGrain,
                ActivationId = ActivationId.NewId(),
                SiloAddress = Silo1,
            },
            validAddress: null));
        var removed = tracker.RemoveForSilo(Silo1);

        var snapshot = Assert.Single(removed!);
        Assert.Null(snapshot.BodyObject);
        Assert.Empty(snapshot.CacheInvalidationHeader!);
    }

    [Fact]
    public void ExplicitTimeToLiveControlsExpiry()
    {
        var timeProvider = new FakeTimeProvider();
        var tracker = CreateTracker(timeProvider, TimeSpan.FromMinutes(1));
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        request.TimeToLive = TimeSpan.FromSeconds(10);
        Assert.True(tracker.Track(request));

        timeProvider.Advance(TimeSpan.FromSeconds(9));
        tracker.RemoveExpired();
        Assert.Equal(1, tracker.Count);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.RemoveExpired();
        Assert.Equal(0, tracker.Count);
        Assert.Null(tracker.RemoveForSilo(Silo1));
    }

    [Fact]
    public void ResponseTimeoutControlsExpiryWhenTimeToLiveIsAbsent()
    {
        var timeProvider = new FakeTimeProvider();
        var tracker = CreateTracker(timeProvider, TimeSpan.FromSeconds(20));
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        Assert.True(tracker.Track(request));

        timeProvider.Advance(TimeSpan.FromSeconds(19));
        tracker.RemoveExpired();
        Assert.Equal(1, tracker.Count);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.RemoveExpired();
        Assert.Equal(0, tracker.Count);
        Assert.Null(tracker.RemoveForSilo(Silo1));
        Assert.False(tracker.TryRemove(request.Id, out _));
    }

    [Fact]
    public void TtlLessRejectionNearFallbackRetentionDeadlineRemainsSendable()
    {
        AssertTtlLessRejectionRemainsSendable(TimeSpan.FromSeconds(19));
    }

    [Fact]
    public void TtlLessRejectionAfterFallbackRetentionDeadlineRemainsSendableFromOutboundQueue()
    {
        AssertTtlLessRejectionRemainsSendable(TimeSpan.FromSeconds(21));
    }

    private static void AssertTtlLessRejectionRemainsSendable(TimeSpan elapsed)
    {
        var timeProvider = new FakeTimeProvider();
        var tracker = CreateTracker(timeProvider, TimeSpan.FromSeconds(20));
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        Assert.Null(request.TimeToLive);
        Assert.True(tracker.Track(request));

        timeProvider.Advance(elapsed);
        var removed = tracker.RemoveForSilo(Silo1);

        var snapshot = Assert.Single(removed!);
        Assert.Null(snapshot.TimeToLive);
        var rejection = CreateResponse(snapshot, Message.ResponseTypes.Rejection);
        Assert.Null(rejection.TimeToLive);
        Assert.False(rejection.IsExpired);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void ExplicitTimeToLivePreservesOnlyTheOriginalRemainingDeadline()
    {
        var timeProvider = new FakeTimeProvider();
        var tracker = CreateTracker(timeProvider, TimeSpan.FromMinutes(1));
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        request.TimeToLive = TimeSpan.FromSeconds(10);
        Assert.True(tracker.Track(request));

        timeProvider.Advance(TimeSpan.FromSeconds(9));
        var removed = tracker.RemoveForSilo(Silo1);

        var snapshot = Assert.Single(removed!);
        Assert.NotNull(snapshot.TimeToLive);
        Assert.InRange(snapshot.TimeToLive.Value, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(1));
        var rejection = CreateResponse(snapshot, Message.ResponseTypes.Rejection);
        Assert.NotNull(rejection.TimeToLive);
        Assert.Equal(0, tracker.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationResponseAndShutdownClearRaceLeavesTrackerEmpty(bool clearFirst)
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        var response = CreateResponse(request, Message.ResponseTypes.Error);
        response.BodyObject = new OperationCanceledException();
        Assert.True(tracker.Track(request));

        if (clearFirst)
        {
            tracker.Clear();
            Assert.False(tracker.TryComplete(response));
        }
        else
        {
            Assert.True(tracker.TryComplete(response));
            tracker.Clear();
        }

        Assert.Equal(0, tracker.Count);
        Assert.Null(tracker.RemoveForSilo(Silo1));
    }

    [Fact]
    public void NonPositiveResponseTimeoutDoesNotTrackRequestWithoutTimeToLive()
    {
        var tracker = CreateTracker(responseTimeout: TimeSpan.Zero);

        var tracked = tracker.Track(CreateMessage(1, Message.Directions.Request, Silo1));

        Assert.False(tracked);
        Assert.Equal(0, tracker.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void NonPositiveResponseTimeoutUsesOneSecondMaintenancePeriod(int responseTimeoutMilliseconds)
    {
        var period = Gateway.GetRequestMaintenancePeriod(TimeSpan.FromMilliseconds(responseTimeoutMilliseconds));

        Assert.Equal(TimeSpan.FromSeconds(1), period);
    }

    [Fact]
    public void PositiveResponseTimeoutControlsMaintenancePeriodUpToOneSecond()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            Gateway.GetRequestMaintenancePeriod(TimeSpan.FromMilliseconds(250)));
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            Gateway.GetRequestMaintenancePeriod(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ResponseForLiveOriginGatewayRoutesThroughItsRequestTracker()
    {
        var response = CreateMessage(1, Message.Directions.Response, Silo1);

        Assert.True(MessageCenter.ShouldRouteResponseViaTargetSilo(response, Silo2));
        Assert.False(MessageCenter.CanDeliverToProxyLocally(response, Silo2, targetSiloIsDead: false));
        Assert.False(MessageCenter.ShouldRouteResponseViaTargetSilo(response, Silo1));
        Assert.True(MessageCenter.CanDeliverToProxyLocally(response, Silo1, targetSiloIsDead: false));
    }

    [Fact]
    public void ResponseForDeadOriginGatewayCanUseLocalProxyDelivery()
    {
        var response = CreateMessage(1, Message.Directions.Response, Silo1);

        Assert.True(MessageCenter.ShouldRouteResponseViaTargetSilo(response, Silo2));
        Assert.True(MessageCenter.CanDeliverToProxyLocally(response, Silo2, targetSiloIsDead: true));
    }

    [Theory]
    [InlineData((int)Message.Directions.Request)]
    [InlineData((int)Message.Directions.OneWay)]
    public void NonResponseMessagesCanUseLocalProxyDelivery(int direction)
    {
        var message = CreateMessage(1, (Message.Directions)direction, Silo1);

        Assert.False(MessageCenter.ShouldRouteResponseViaTargetSilo(message, Silo2));
        Assert.True(MessageCenter.CanDeliverToProxyLocally(message, Silo2, targetSiloIsDead: false));
    }

    private static GatewayInFlightRequestTracker CreateTracker(
        TimeProvider? timeProvider = null,
        TimeSpan? responseTimeout = null) =>
        new(timeProvider ?? TimeProvider.System, responseTimeout ?? TimeSpan.FromSeconds(30));

    private static Message CreateMessage(long id, Message.Directions direction, SiloAddress targetSilo) =>
        new()
        {
            Id = new CorrelationId(id),
            Direction = direction,
            TargetSilo = targetSilo,
            TargetGrain = GrainId.Create("target", id.ToString()),
        };

    private static Message CreateResponse(Message request, Message.ResponseTypes responseType) =>
        new()
        {
            Id = request.Id,
            Direction = Message.Directions.Response,
            Result = responseType,
            TimeToLive = request.TimeToLive,
        };

}
