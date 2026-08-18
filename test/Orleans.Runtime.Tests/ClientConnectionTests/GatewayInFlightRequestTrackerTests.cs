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
    private readonly TestClient _client = new();

    [Fact, TestCategory("BVT")]
    public void OneWayMessagesAreNotTracked()
    {
        var tracker = CreateTracker();

        var tracked = tracker.Track(_client, CreateMessage(1, Message.Directions.OneWay, Silo1));

        Assert.False(tracked);
        Assert.Equal(0, tracker.Count);
    }

    [Fact, TestCategory("BVT")]
    public void RequestsWithoutFinalDestinationAreNotTracked()
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        request.TargetSilo = null;

        var tracked = tracker.Track(_client, request);

        Assert.False(tracked);
        Assert.Equal(0, tracker.Count);
    }

    [Fact, TestCategory("BVT")]
    public void SystemTargetRequestsAreNotTracked()
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        request.TargetGrain = SystemTargetGrainId.Create(Constants.CatalogType, Silo1).GrainId;

        var tracked = tracker.Track(_client, request);

        Assert.False(tracked);
        Assert.Equal(0, tracker.Count);
    }

    [Fact, TestCategory("BVT")]
    public void StatusResponseRetainsRequest()
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        Assert.True(tracker.Track(_client, request));

        var completed = tracker.TryComplete(_client, CreateResponse(request, Message.ResponseTypes.Status));

        Assert.False(completed);
        Assert.Equal(1, tracker.Count);
    }

    [Theory, TestCategory("BVT")]
    [InlineData((int)Message.ResponseTypes.Success)]
    [InlineData((int)Message.ResponseTypes.Error)]
    [InlineData((int)Message.ResponseTypes.Rejection)]
    public void TerminalResponseRemovesRequest(int responseType)
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        Assert.True(tracker.Track(_client, request));

        var completed = tracker.TryComplete(_client, CreateResponse(request, (Message.ResponseTypes)responseType));

        Assert.True(completed);
        Assert.Equal(0, tracker.Count);
    }

    [Fact, TestCategory("BVT")]
    public void NonResponseMessageDoesNotCompleteRequest()
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        Assert.True(tracker.Track(_client, request));

        var completed = tracker.TryComplete(_client, request);

        Assert.False(completed);
        Assert.Equal(1, tracker.Count);
    }

    [Fact, TestCategory("BVT")]
    public void RemoveForSiloRemovesOnlyRequestsForThatDestination()
    {
        var tracker = CreateTracker();
        var request1 = CreateMessage(1, Message.Directions.Request, Silo1);
        var request2 = CreateMessage(2, Message.Directions.Request, Silo2);
        var request3 = CreateMessage(3, Message.Directions.Request, Silo1);
        var otherClient = new TestClient();
        Assert.True(tracker.Track(_client, request1));
        Assert.True(tracker.Track(otherClient, request2));
        Assert.True(tracker.Track(_client, request3));
        Assert.Equal(2, tracker.ActiveClientCount);

        var removed = tracker.RemoveForSilo(Silo1);

        Assert.NotNull(removed);
        Assert.Equal(2, removed.Count);
        Assert.All(removed, entry => Assert.Same(_client, entry.Client));
        Assert.Contains(removed, entry => entry.Request.Id == request1.Id && Silo1.Equals(entry.Request.TargetSilo));
        Assert.Contains(removed, entry => entry.Request.Id == request3.Id && Silo1.Equals(entry.Request.TargetSilo));
        Assert.Equal(1, tracker.Count);
        Assert.Equal(1, tracker.ActiveClientCount);
        Assert.True(tracker.TryComplete(otherClient, CreateResponse(request2, Message.ResponseTypes.Success)));
        Assert.Equal(0, tracker.Count);
        Assert.Equal(0, tracker.ActiveClientCount);
    }

    [Fact, TestCategory("BVT")]
    public void ClearRemovesAllRequestsOnDisconnect()
    {
        var tracker = CreateTracker();
        var request1 = CreateMessage(1, Message.Directions.Request, Silo1);
        var request2 = CreateMessage(2, Message.Directions.Request, Silo2);
        var otherClient = new TestClient();
        Assert.True(tracker.Track(_client, request1));
        Assert.True(tracker.Track(otherClient, request2));

        tracker.Clear(_client);

        Assert.Equal(1, tracker.Count);
        Assert.Equal(1, tracker.ActiveClientCount);
        Assert.False(tracker.TryComplete(_client, CreateResponse(request1, Message.ResponseTypes.Success)));
        Assert.True(tracker.TryComplete(otherClient, CreateResponse(request2, Message.ResponseTypes.Error)));
        Assert.Equal(0, tracker.ActiveClientCount);
    }

    [Fact, TestCategory("BVT")]
    public void SnapshotDoesNotRetainBodyOrMutableCacheHeader()
    {
        var tracker = CreateTracker();
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        request.BodyObject = new object();
        request.CacheInvalidationHeader = [];
        Assert.True(tracker.Track(_client, request));

        request.CacheInvalidationHeader.Add(new GrainAddressCacheUpdate(
            new GrainAddress
            {
                GrainId = request.TargetGrain,
                ActivationId = ActivationId.NewId(),
                SiloAddress = Silo1,
            },
            validAddress: null));
        var removed = tracker.RemoveForSilo(Silo1);

        var snapshot = Assert.Single(removed!).Request;
        Assert.Null(snapshot.BodyObject);
        Assert.Empty(snapshot.CacheInvalidationHeader!);
    }

    [Fact, TestCategory("BVT")]
    public void ExplicitTimeToLiveControlsExpiry()
    {
        var timeProvider = new FakeTimeProvider();
        var tracker = CreateTracker(timeProvider, TimeSpan.FromMinutes(1));
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        request.TimeToLive = TimeSpan.FromSeconds(10);
        Assert.True(tracker.Track(_client, request));

        timeProvider.Advance(TimeSpan.FromSeconds(9));
        tracker.RemoveExpired();
        Assert.Equal(1, tracker.Count);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.RemoveExpired();
        Assert.Equal(0, tracker.Count);
    }

    [Fact, TestCategory("BVT")]
    public void ResponseTimeoutControlsExpiryWhenTimeToLiveIsAbsent()
    {
        var timeProvider = new FakeTimeProvider();
        var tracker = CreateTracker(timeProvider, TimeSpan.FromSeconds(20));
        Assert.True(tracker.Track(_client, CreateMessage(1, Message.Directions.Request, Silo1)));

        timeProvider.Advance(TimeSpan.FromSeconds(19));
        tracker.RemoveExpired();
        Assert.Equal(1, tracker.Count);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.RemoveExpired();
        Assert.Equal(0, tracker.Count);
    }

    [Fact, TestCategory("BVT")]
    public void NonPositiveResponseTimeoutDoesNotTrackRequestWithoutTimeToLive()
    {
        var tracker = CreateTracker(responseTimeout: TimeSpan.Zero);

        var tracked = tracker.Track(_client, CreateMessage(1, Message.Directions.Request, Silo1));

        Assert.False(tracked);
        Assert.Equal(0, tracker.Count);
    }

    private static GatewayInFlightRequestTracker<TestClient> CreateTracker(
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
        };

    private sealed class TestClient
    {
    }
}
