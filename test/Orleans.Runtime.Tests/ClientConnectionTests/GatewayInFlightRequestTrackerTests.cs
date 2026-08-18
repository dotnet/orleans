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

    [Fact, TestCategory("BVT")]
    public void TracksRequestsUntilTerminalResponse()
    {
        var tracker = new Gateway.InFlightRequestTracker(TimeProvider.System, TimeSpan.FromSeconds(30));
        var request = CreateMessage(1, Message.Directions.Request, Silo1);
        var oneWay = CreateMessage(2, Message.Directions.OneWay, Silo1);

        tracker.Track(request);
        tracker.Track(oneWay);

        Assert.Equal(1, tracker.Count);
        Assert.False(tracker.TryComplete(CreateResponse(request, Message.ResponseTypes.Status)));
        Assert.Equal(1, tracker.Count);
        Assert.True(tracker.TryComplete(CreateResponse(request, Message.ResponseTypes.Success)));
        Assert.Equal(0, tracker.Count);
    }

    [Fact, TestCategory("BVT")]
    public void RemovesRequestsForDeadSiloAndOnClear()
    {
        var tracker = new Gateway.InFlightRequestTracker(TimeProvider.System, TimeSpan.FromSeconds(30));
        var request1 = CreateMessage(1, Message.Directions.Request, Silo1);
        var request2 = CreateMessage(2, Message.Directions.Request, Silo2);
        tracker.Track(request1);
        tracker.Track(request2);

        var removed = tracker.RemoveForSilo(Silo1);

        Assert.Equal(request1.Id, Assert.Single(removed!).Id);
        Assert.Equal(1, tracker.Count);
        tracker.Clear();
        Assert.Equal(0, tracker.Count);
    }

    [Fact, TestCategory("BVT")]
    public void RemovesRequestsAtResponseTimeout()
    {
        var timeProvider = new FakeTimeProvider();
        var tracker = new Gateway.InFlightRequestTracker(timeProvider, TimeSpan.FromSeconds(10));
        tracker.Track(CreateMessage(1, Message.Directions.Request, Silo1));

        timeProvider.Advance(TimeSpan.FromSeconds(9));
        tracker.RemoveExpired();
        Assert.Equal(1, tracker.Count);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.RemoveExpired();
        Assert.Equal(0, tracker.Count);
    }

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
}
