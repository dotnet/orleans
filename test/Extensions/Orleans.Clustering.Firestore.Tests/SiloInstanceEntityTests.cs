using Orleans.Runtime;
using TestExtensions;

namespace Orleans.Clustering.Firestore.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("GoogleCloud")]
[TestArea("Clustering")]
public class SiloInstanceEntityTests
{
    public static TheoryData<SiloStatus> SiloStatuses => new(Enum.GetValues<SiloStatus>());

    [Theory]
    [MemberData(nameof(SiloStatuses))]
    public void MembershipEntryRoundTripsWithNumericStatus(SiloStatus status)
    {
        var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var iAmAliveTime = startTime.AddMinutes(1);
        var suspectTime = startTime.AddMinutes(2);
        var membershipEntry = new MembershipEntry
        {
            SiloAddress = SiloAddressUtils.NewLocalSiloAddress(1),
            ProxyPort = 30000,
            HostName = "localhost",
            SiloName = "TestSilo",
            RoleName = "TestRole",
            UpdateZone = 3,
            FaultZone = 5,
            Status = status,
            StartTime = startTime,
            IAmAliveTime = iAmAliveTime,
        };
        var suspectingSilo = SiloAddressUtils.NewLocalSiloAddress(2);
        membershipEntry.AddSuspector(suspectingSilo, suspectTime);
        membershipEntry.AddSuspector(suspectingSilo, suspectTime.AddMinutes(1));

        var entity = SiloInstanceEntity.FromMembershipEntry(membershipEntry, membershipVersion: 42);
        var result = entity.ToMembershipEntry();

        Assert.Equal((int)status, entity.Status);
        Assert.Equal(42, entity.MembershipVersion);
        Assert.Equal(membershipEntry.SiloAddress, result.SiloAddress);
        Assert.Equal(membershipEntry.ProxyPort, result.ProxyPort);
        Assert.Equal(membershipEntry.HostName, result.HostName);
        Assert.Equal(membershipEntry.SiloName, result.SiloName);
        Assert.Equal(membershipEntry.RoleName, result.RoleName);
        Assert.Equal(membershipEntry.UpdateZone, result.UpdateZone);
        Assert.Equal(membershipEntry.FaultZone, result.FaultZone);
        Assert.Equal(membershipEntry.Status, result.Status);
        Assert.Equal(membershipEntry.StartTime, result.StartTime);
        Assert.Equal(membershipEntry.IAmAliveTime, result.IAmAliveTime);
        Assert.Equal(membershipEntry.SuspectTimes, result.SuspectTimes);
    }
}
