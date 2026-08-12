using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
public class PhiAccrualFailureDetectorTests
{
    [Fact]
    public void UsesInitialTimeoutUntilEnoughEvidenceIsAvailable()
    {
        var detector = new PhiAccrualFailureDetector(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 3; i++)
        {
            detector.RecordResponseTime(TimeSpan.Zero);
        }

        Assert.Equal(TimeSpan.FromSeconds(5), detector.GetTimeout());
    }

    [Fact]
    public void StableFastResponsesLowerTimeout()
    {
        var detector = new PhiAccrualFailureDetector(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 4; i++)
        {
            detector.RecordResponseTime(TimeSpan.Zero);
        }

        Assert.Equal(TimeSpan.FromSeconds(2.5), detector.GetTimeout());
    }

    [Fact]
    public void StableSlowResponsesRaiseTimeout()
    {
        var detector = new PhiAccrualFailureDetector(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 4; i++)
        {
            detector.RecordResponseTime(TimeSpan.FromSeconds(4));
        }

        Assert.Equal(TimeSpan.FromSeconds(6.5), detector.GetTimeout());
    }

    [Fact]
    public void EffectiveTimeoutIsClampedAfterExtensions()
    {
        var detector = new PhiAccrualFailureDetector(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 4; i++)
        {
            detector.RecordResponseTime(TimeSpan.FromSeconds(4));
        }

        var timeout = detector.GetTimeout(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(10), extensionFactor: 2);

        Assert.Equal(TimeSpan.FromSeconds(10), timeout);
    }

    [Fact]
    public void HistoryIsBounded()
    {
        var detector = new PhiAccrualFailureDetector(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100; i++)
        {
            detector.RecordResponseTime(TimeSpan.Zero);
        }

        for (var i = 0; i < 100; i++)
        {
            detector.RecordResponseTime(TimeSpan.FromSeconds(4));
        }

        Assert.Equal(100, detector.SampleCount);
        Assert.Equal(TimeSpan.FromSeconds(6.5), detector.GetTimeout());
    }

    [Theory]
    [InlineData(0, true, false, 5)]
    [InlineData(1, true, false, 10)]
    [InlineData(0, false, false, 10)]
    [InlineData(0, true, true, 130)]
    public void MonitorExtensionsAndClampAreAppliedInOrder(
        int localDegradationScore,
        bool isDirectProbe,
        bool isDebuggerAttached,
        double expectedSeconds)
    {
        var detector = new PhiAccrualFailureDetector(TimeSpan.FromSeconds(5));
        var options = new ClusterMembershipOptions
        {
            MinProbeTimeout = TimeSpan.FromSeconds(2.5),
            MaxProbeTimeout = TimeSpan.FromSeconds(10),
        };

        var timeout = SiloHealthMonitor.CalculateProbeTimeout(
            detector,
            options,
            localDegradationScore,
            isDirectProbe,
            isDebuggerAttached);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timeout);
    }
}
