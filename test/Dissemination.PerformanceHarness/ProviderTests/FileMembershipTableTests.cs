using System.Net;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Dissemination.PerformanceHarness;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public sealed class FileMembershipTableTests : IDisposable
{
    private static readonly DateTime Cutoff = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"orleans-dissemination-cleanup-{Guid.NewGuid():N}");

    [Fact]
    public async Task CleanupRemovesEveryExpiredNonActiveStatus()
    {
        var table = CreateTable();
        await table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken);
        foreach (var status in Enum.GetValues<SiloStatus>())
        {
            var entry = CreateEntry(status, (int)status + 1);
            var data = await table.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.True(await table.InsertRowAsync(entry, data.Version.Next(), TestContext.Current.CancellationToken));
        }

        var before = await table.ReadAllAsync(TestContext.Current.CancellationToken);
        var expected = Assert.Single(before.Members, row => row.Item1.Status == SiloStatus.Active);
        var reopened = CreateTable();
        await reopened.CleanupDefunctSiloEntriesAsync(Cutoff, TestContext.Current.CancellationToken);
        var after = await reopened.ReadAllAsync(TestContext.Current.CancellationToken);

        var remaining = Assert.Single(after.Members);
        Assert.Equal(expected.Item1.SiloAddress, remaining.Item1.SiloAddress);
        Assert.Equal(SiloStatus.Active, remaining.Item1.Status);
        Assert.Equal(expected.Item2, remaining.Item2);
        Assert.Equal(before.Version.Version, after.Version.Version);
        Assert.Equal(before.Version.VersionEtag, after.Version.VersionEtag);
    }

    [Theory]
    [InlineData("start", -1)]
    [InlineData("start", 0)]
    [InlineData("start", 1)]
    [InlineData("heartbeat", -1)]
    [InlineData("heartbeat", 0)]
    [InlineData("heartbeat", 1)]
    [InlineData("suspect", -1)]
    [InlineData("suspect", 0)]
    [InlineData("suspect", 1)]
    public async Task CleanupUsesEffectiveUpdateTimeAndExclusiveCutoff(string timestamp, int offset)
    {
        var table = CreateTable();
        await table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken);
        var entry = CreateEntry(SiloStatus.Dead, 1);
        var latest = Cutoff.AddTicks(offset);
        switch (timestamp)
        {
            case "start":
                entry.StartTime = latest;
                break;
            case "heartbeat":
                entry.IAmAliveTime = latest;
                break;
            case "suspect":
                entry.SuspectTimes =
                [
                    Tuple.Create(CreateEntry(SiloStatus.Active, 2).SiloAddress, latest),
                    Tuple.Create(CreateEntry(SiloStatus.Active, 3).SiloAddress, Cutoff.AddDays(-2)),
                ];
                break;
        }

        var data = await table.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.True(await table.InsertRowAsync(entry, data.Version.Next(), TestContext.Current.CancellationToken));
        var reopened = CreateTable();
        var persisted = Assert.Single((await reopened.ReadAllAsync(TestContext.Current.CancellationToken)).Members);
        Assert.Equal(latest, persisted.Item1.EffectiveUpdateTime);

        await reopened.CleanupDefunctSiloEntriesAsync(Cutoff, TestContext.Current.CancellationToken);
        var after = await reopened.ReadAllAsync(TestContext.Current.CancellationToken);
        if (offset < 0)
        {
            Assert.Empty(after.Members);
        }
        else
        {
            var remaining = Assert.Single(after.Members);
            Assert.Equal(entry.SiloAddress, remaining.Item1.SiloAddress);
            Assert.Equal(latest, remaining.Item1.EffectiveUpdateTime);
            Assert.Equal(persisted.Item2, remaining.Item2);
        }
    }

    private FileMembershipTable CreateTable() =>
        new(new("cleanup", "cleanup", _directory, 0, Environment.ProcessId, Enabled: false));

    private static MembershipEntry CreateEntry(SiloStatus status, int port) => new()
    {
        SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 10000 + port), 1),
        HostName = "localhost",
        SiloName = $"cleanup-{port}",
        Status = status,
        StartTime = Cutoff.AddDays(-1),
        IAmAliveTime = Cutoff.AddDays(-1),
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
