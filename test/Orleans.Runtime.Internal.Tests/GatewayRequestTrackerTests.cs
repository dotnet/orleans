using System;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class GatewayRequestTrackerTests
{
    [Fact]
    public void DropExpiredMessagesFalse_RequestIsRemovedAtExplicitDeadline()
    {
        var timeProvider = new ManualTimeProvider();
        var responseTimeout = TimeSpan.FromSeconds(30);
        var tracker = new GatewayRequestTracker(timeProvider, TimeSpan.FromMinutes(5));
        var request = CreateRequest();
        request.SetGatewayRequestTimeout(responseTimeout);

        Assert.Null(request.TimeToLive);
        tracker.Register(request);
        Assert.Equal(1, tracker.Count);

        timeProvider.Advance(responseTimeout);
        tracker.RemoveExpired();

        Assert.Equal(0, tracker.Count);
        Assert.Null(request.TimeToLive);
    }

    [Fact]
    public void ResponseBucketedToDifferentGateway_CompletesOwningGatewayWithoutLaterRejection()
    {
        var ownerGateway = CreateSiloAddress(11111);
        var bucketGateway = CreateSiloAddress(22222);
        var spoofedGateway = CreateSiloAddress(33333);
        var tracker = new GatewayRequestTracker(TimeProvider.System, TimeSpan.FromSeconds(30));
        var request = CreateRequest();
        request.SendingSilo = bucketGateway;
        request.RequestContextData = new()
        {
            ["#orleans.gateway.request-owner"] = spoofedGateway,
            ["#orleans.gateway.request-owner-silo"] = spoofedGateway,
            ["#orleans.gateway.response-target"] = spoofedGateway,
        };
        request.ClearGatewayRequestOwner();
        request.SetGatewayRequestOwner(ownerGateway, CreateSiloAddress(44444));
        Assert.Equal(ownerGateway, request.SendingSilo);
        tracker.Register(request);

        var response = new Message
        {
            Direction = Message.Directions.Response,
            Id = request.Id,
            SendingGrain = request.TargetGrain,
            TargetGrain = request.SendingGrain,
            TargetSilo = bucketGateway,
        };

        response.ApplyGatewayRequestOwner(request);
        Assert.Equal(ownerGateway, response.TargetSilo);
        Assert.True(response.TryGetGatewayRequestOwner(out var restoredOwner, out _));
        Assert.Equal(ownerGateway, restoredOwner);
        response.RestoreGatewayResponseTarget();
        Assert.Equal(bucketGateway, response.TargetSilo);
        Assert.True(tracker.Complete(response));
        Assert.Equal(0, tracker.Count);
        Assert.Empty(tracker.Drain());
    }

    private static Message CreateRequest() => new()
    {
        Direction = Message.Directions.Request,
        Id = new CorrelationId(1234),
        SendingGrain = GrainId.Create("source", "1"),
        TargetGrain = GrainId.Create("client", "2"),
    };

    private static SiloAddress CreateSiloAddress(int port)
        => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), 1);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
