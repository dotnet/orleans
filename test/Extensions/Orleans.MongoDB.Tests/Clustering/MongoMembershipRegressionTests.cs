using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Clustering.MongoDB;
using Orleans.Providers.MongoDB.Membership;
using Orleans.Runtime;
using Xunit;

namespace Orleans.MongoDB.Tests.Clustering;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Membership")]
public sealed class MongoMembershipRegressionTests
{
    [Theory]
    [InlineData(MongoDBMembershipStrategy.SingleDocument)]
    [InlineData(MongoDBMembershipStrategy.Multiple)]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated)]
    public async Task AlreadyCancelledNativeOperations_DoNoIoAndPreserveMembership(MongoDBMembershipStrategy strategy)
    {
        var commands = new ConcurrentQueue<string>();
        await using var database = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e => commands.Enqueue(e.CommandName)));
        var table = database.CreateMembershipTable("cancelled", strategy);
        var other = database.CreateMembershipTable("neighbor", strategy);
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        await other.InitializeMembershipTableAsync(true, token);
        var entry = Entry(1, SiloStatus.Dead, 30001);
        await Insert(table, entry, token);
        await Insert(table, Entry(2, SiloStatus.Active, 30002), token);
        await Insert(other, Entry(3, SiloStatus.Active, 30003), token);
        var before = await table.ReadAllAsync(token);
        var dead = Assert.Single(before.Members, row => row.Item1.Status == SiloStatus.Dead);
        var update = Entry(1, SiloStatus.Active, 40001);
        update.HostName = "should-not-commit";
        update.IAmAliveTime = entry.IAmAliveTime.AddDays(1);
        commands.Clear();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.Cancel();
        var cancelled = cancellation.Token;
        Func<Task>[] operations =
        [
            () => table.InitializeMembershipTableAsync(true, cancelled),
            () => table.ReadAllAsync(cancelled),
#pragma warning disable CS0618 // Explicit regression for the legacy native cancellation entry point.
            () => table.ReadRowAsync(entry.SiloAddress, cancelled),
#pragma warning restore CS0618
            () => table.InsertRowAsync(Entry(4, SiloStatus.Active, 30004), before.Version.Next(), cancelled),
            () => table.UpdateRowAsync(update, dead.Item2, before.Version.Next(), cancelled),
            () => table.UpdateIAmAliveAsync(update, cancelled),
            () => table.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), cancelled),
            () => table.DeleteMembershipTableEntriesAsync("cancelled", cancelled)
        ];
        foreach (var operation in operations)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
            Assert.Equal(cancelled, error.CancellationToken);
        }
        Assert.Empty(commands);
        var after = await table.ReadAllAsync(token);
        Assert.Equal(before.Version.Version, after.Version.Version);
        Assert.Equal(before.Version.VersionEtag, after.Version.VersionEtag);
        Assert.Equal(2, after.Members.Count);
        foreach (var original in before.Members)
        {
            var retained = Assert.Single(after.Members, row => row.Item1.SiloAddress.Equals(original.Item1.SiloAddress));
            Assert.Equal(original.Item2, retained.Item2);
            Assert.Equal(original.Item1.Status, retained.Item1.Status);
            Assert.Equal(original.Item1.HostName, retained.Item1.HostName);
            Assert.Equal(original.Item1.ProxyPort, retained.Item1.ProxyPort);
            Assert.Equal(original.Item1.StartTime, retained.Item1.StartTime);
            Assert.Equal(original.Item1.IAmAliveTime, retained.Item1.IAmAliveTime);
        }
        Assert.Equal(30003, Assert.Single((await other.ReadAllAsync(token)).Members).Item1.ProxyPort);
    }

    [Theory]
    [InlineData(MongoDBMembershipStrategy.SingleDocument)]
    [InlineData(MongoDBMembershipStrategy.Multiple)]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated)]
    public async Task GetGateways_ReturnsOnlyActiveProxyRowsInConfiguredCluster(MongoDBMembershipStrategy strategy)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var table = database.CreateMembershipTable("primary", strategy);
        var other = database.CreateMembershipTable("other", strategy);
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        await other.InitializeMembershipTableAsync(true, token);
        var active = Entry(1, SiloStatus.Active, 30001);
        await Insert(table, active, token);
        await Insert(table, Entry(2, SiloStatus.Dead, 30002), token);
        await Insert(table, Entry(3, SiloStatus.Joining, 30003), token);
        await Insert(table, Entry(4, SiloStatus.Active, 0), token);
        await Insert(table, Entry(6, SiloStatus.Active, -1), token);
        await Insert(other, Entry(5, SiloStatus.Active, 30005), token);
        IGatewayListProvider gateway = new MongoGatewayListProvider(database.Client,
            NullLogger<MongoGatewayListProvider>.Instance,
            Options.Create(new ClusterOptions { ClusterId = "primary", ServiceId = "mongo-test" }),
            Options.Create(new GatewayOptions { GatewayListRefreshPeriod = TimeSpan.FromSeconds(19) }),
            Options.Create(new MongoDBGatewayListProviderOptions { DatabaseName = database.Name, Strategy = strategy }));
        await gateway.InitializeGatewayListProvider();
        Assert.Equal(TimeSpan.FromSeconds(19), gateway.MaxStaleness);
        var uri = Assert.Single(await gateway.GetGateways());
        Assert.Equal(SiloAddress.New(IPAddress.Loopback, active.ProxyPort, active.SiloAddress.Generation).ToGatewayUri(), uri);
    }

    [Fact]
    public async Task MultipleDeprecated_PreservesLegacyConstantVersionAndRowEtagConflicts()
    {
        await using var database = await MongoDatabase.CreateAsync();
        var table = database.CreateMembershipTable("legacy", MongoDBMembershipStrategy.MultipleDeprecated);
        var other = database.CreateMembershipTable("other", MongoDBMembershipStrategy.MultipleDeprecated);
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        await other.InitializeMembershipTableAsync(true, token);
        var entry = Entry(1, SiloStatus.Active, 30001);
        await Insert(table, entry, token);
        await Insert(other, Entry(1, SiloStatus.Active, 30002), token);
        var before = await table.ReadAllAsync(token);
        Assert.Equal(0, before.Version.Version);
        Assert.Equal("0", before.Version.VersionEtag);
        var row = Assert.Single(before.Members);
        var staleTag = row.Item2;
        Assert.False(await table.InsertRowAsync(entry, before.Version.Next(), token));
        entry.HostName = "updated";
        Assert.True(await table.UpdateRowAsync(entry, staleTag, before.Version.Next(), token));
        Assert.False(await table.UpdateRowAsync(entry, staleTag, before.Version.Next(), token));
        var updated = Assert.Single((await table.ReadAllAsync(token)).Members);
        Assert.Equal("updated", updated.Item1.HostName);
        Assert.NotEqual(staleTag, updated.Item2);
        var heartbeat = entry.IAmAliveTime.AddMinutes(1);
        await table.UpdateIAmAliveAsync(new MembershipEntry { SiloAddress = entry.SiloAddress, IAmAliveTime = heartbeat }, token);
        var after = await table.ReadAllAsync(token);
        Assert.Equal(0, after.Version.Version);
        Assert.Equal("0", after.Version.VersionEtag);
        var alive = Assert.Single(after.Members);
        Assert.Equal(heartbeat, alive.Item1.IAmAliveTime);
        Assert.Equal(updated.Item2, alive.Item2);
        Assert.Equal("updated", alive.Item1.HostName);
        await table.DeleteMembershipTableEntriesAsync("legacy", token);
        Assert.Empty((await table.ReadAllAsync(token)).Members);
        Assert.Equal(30002, Assert.Single((await other.ReadAllAsync(token)).Members).Item1.ProxyPort);
    }

    private static MembershipEntry Entry(int number, SiloStatus status, int proxyPort) => new()
    {
        SiloAddress = SiloAddress.New(IPAddress.Loopback, 11100 + number, 10 + number),
        HostName = $"host-{number}",
        SiloName = $"silo-{number}",
        Status = status,
        ProxyPort = proxyPort,
        StartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IAmAliveTime = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)
    };

    private static async Task Insert(IMembershipTable table, MembershipEntry entry, CancellationToken token)
        => Assert.True(await table.InsertRowAsync(entry, (await table.ReadAllAsync(token)).Version.Next(), token));

    [Fact]
    public async Task MultipleDeprecated_DeletedRowStaleUpdateDoesNotRecreateMember()
    {
        await using var database = await MongoDatabase.CreateAsync();
        var table = database.CreateMembershipTable("removed", MongoDBMembershipStrategy.MultipleDeprecated);
        var other = database.CreateMembershipTable("neighbor", MongoDBMembershipStrategy.MultipleDeprecated);
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        await other.InitializeMembershipTableAsync(true, token);
        var entry = Entry(1, SiloStatus.Active, 30001);
        await Insert(table, entry, token);
        await Insert(other, Entry(1, SiloStatus.Active, 30002), token);
        var before = await table.ReadAllAsync(token);
        var staleTag = Assert.Single(before.Members).Item2;
        await table.DeleteMembershipTableEntriesAsync("removed", token);
        entry.HostName = "stale-writer";
        Assert.False(await table.UpdateRowAsync(entry, staleTag, before.Version.Next(), token));
        var after = await table.ReadAllAsync(token);
        Assert.Empty(after.Members);
        Assert.Equal(0, after.Version.Version);
        Assert.Equal("0", after.Version.VersionEtag);
        var retained = Assert.Single((await other.ReadAllAsync(token)).Members);
        Assert.Equal(30002, retained.Item1.ProxyPort);
        Assert.Equal("host-1", retained.Item1.HostName);
        Assert.Equal("stale-writer", entry.HostName);
    }

    [Fact]
    public void MembershipStrategy_InvalidValueThrowsBeforeCreatingCollection()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        using var client = new MongoClient(settings);
        var exception = Assert.Throws<ArgumentException>(() =>
            Orleans.Providers.MongoDB.Membership.Store.Factory.CreateCollection(
                client, new MongoDBMembershipTableOptions { DatabaseName = "unused" }, (MongoDBMembershipStrategy)99));
        Assert.Equal("strategy", exception.ParamName);
        Assert.Contains("Invalid strategy", exception.Message);
    }
}
