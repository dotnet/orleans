using System.Net;
using Orleans.Runtime;

namespace Orleans.Clustering.TestKit;

internal static class MembershipTableTestData
{
    internal static readonly DateTime T0 = new(2024, 1, 2, 3, 4, 0, DateTimeKind.Utc);
    internal static readonly DateTime T1 = T0.AddMinutes(1);
    internal static readonly DateTime T2 = T0.AddMinutes(2);

    internal static MembershipEntry CreateEntry(int index, int seed = 0, SiloStatus status = SiloStatus.Created) => new()
    {
        SiloAddress = SiloAddress.New(IPAddress.Loopback, 12000 + index, 100 + (seed & 0x7fff)),
        Status = status, ProxyPort = 22000 + index, HostName = $"host-{index}", SiloName = $"silo-{index}",
        // Explicit defaults round-trip across providers which do not store Azure deployment metadata.
        RoleName = string.Empty, UpdateZone = 0, FaultZone = 0,
        StartTime = T0.AddMinutes(-1), IAmAliveTime = T0,
        SuspectTimes = [Tuple.Create(SiloAddress.New(IPAddress.Loopback, 11001, 10), T0.AddSeconds(-10)),
            Tuple.Create(SiloAddress.New(IPAddress.Loopback, 11002, 11), T0.AddSeconds(-5))]
    };

    internal static MembershipEntry Copy(MembershipEntry entry) => MembershipEntrySnapshot.Capture(entry).ToEntry();

    internal static MembershipEntry CreateSuccessor(MembershipEntry predecessor)
    {
        var result = Copy(predecessor);
        result.SiloAddress = SiloAddress.New(predecessor.SiloAddress.Endpoint.Address, predecessor.SiloAddress.Endpoint.Port,
            checked(predecessor.SiloAddress.Generation + 1));
        result.Status = SiloStatus.Created;
        result.StartTime = T2;
        result.IAmAliveTime = T2;
        result.SiloName += "-successor";
        result.SuspectTimes = [];
        return result;
    }

    internal static MembershipEntry Forward(MembershipEntry entry)
    {
        var result = Copy(entry);
        ClusteringTestKitDiagnostics.Require(entry.Status is >= SiloStatus.Created and < SiloStatus.Dead,
            $"no legal forward transition for {entry.Status}");
        result.Status = entry.Status + 1;
        result.HostName += "-updated";
        result.SiloName += "-updated";
        result.ProxyPort++;
        result.AddOrUpdateSuspector(SiloAddress.New(IPAddress.Loopback, 11003, 12), T0, maxVotes: 10);
        return result;
    }

    internal static IReadOnlyList<MembershipEntry> CreateCleanupEntries(int seed = 0)
    {
        var result = Enum.GetValues<SiloStatus>()
            .Where(status => status != SiloStatus.None)
            .Select(status => CreateEntry(100 + (int)status, seed, status)).ToList();
        var start = CreateEntry(110, seed, SiloStatus.Dead);
        start.StartTime = T2;
        var heartbeat = CreateEntry(111, seed, SiloStatus.Dead);
        heartbeat.IAmAliveTime = T2;
        var vote = CreateEntry(112, seed, SiloStatus.Dead);
        vote.SuspectTimes!.Insert(0, Tuple.Create(CreateEntry(1).SiloAddress, T2));
        var boundary = CreateEntry(113, seed, SiloStatus.Dead);
        boundary.IAmAliveTime = T1;
        result.AddRange([start, heartbeat, vote, boundary]);
        return result;
    }

    internal static void MutateCaller(MembershipEntry entry)
    {
        entry.HostName = "caller-only";
        entry.SiloName = "caller-name";
        entry.ProxyPort = 29999;
        entry.RoleName = "caller-role";
        entry.UpdateZone = 13;
        entry.FaultZone = 17;
        entry.Status = SiloStatus.Dead;
        entry.StartTime = T2;
        entry.IAmAliveTime = T2;
        entry.SuspectTimes ??= [];
        entry.SuspectTimes.Add(Tuple.Create(CreateEntry(50).SiloAddress, T2));
        entry.SuspectTimes[0] = Tuple.Create(CreateEntry(51).SiloAddress, T2);
        entry.SuspectTimes = [Tuple.Create(CreateEntry(52).SiloAddress, T2)];
    }
}
