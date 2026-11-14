using System.Net;
using MongoDB.Driver;
using Orleans.Clustering.MongoDB;
using Orleans.Providers.MongoDB.Membership.Store;
using Orleans.Runtime;
using Xunit;

namespace Orleans.MongoDB.Tests.Clustering;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Membership")]
public sealed class MongoMembershipIdentityTests
{
    [Theory]
    [InlineData(MongoDBMembershipStrategy.Multiple, "2001:db8::c000:201", "192.0.2.1")]
    [InlineData(MongoDBMembershipStrategy.Multiple, "192.0.2.1", "2001:db8::c000:201")]
    [InlineData(MongoDBMembershipStrategy.Multiple, "2001:db8::c000:201", "2001:db9::c000:201")]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated, "2001:db8::c000:201", "192.0.2.1")]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated, "192.0.2.1", "2001:db8::c000:201")]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated, "2001:db8::c000:201", "2001:db9::c000:201")]
    public async Task LegacyAddressCollision_FailsExplicitlyAndPreservesStoredIdentity(
        MongoDBMembershipStrategy strategy, string originalIp, string conflictingIp)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var table = database.CreateMembershipTable("identity", strategy);
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var original = Entry(originalIp);
        var conflicting = Entry(conflictingIp);
        var id = $"identity@{original.SiloAddress.Endpoint.Address.MapToIPv4()}:11111/1";
        Assert.Equal(id, MongoMembershipDocument.GetId("identity", original.SiloAddress));
        Assert.Equal(id, MongoMembershipDocument.GetId("identity", conflicting.SiloAddress));
        Assert.True(await table.InsertRowAsync(original, (await table.ReadAllAsync(token)).Version.Next(), token));
        var before = await table.ReadAllAsync(token);
        var row = Assert.Single(before.Members);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => table.InsertRowAsync(conflicting, before.Version.Next(), token));
        Assert.Contains("key collision", error.Message);
        Assert.Contains(original.SiloAddress.ToParsableString(), error.Message);
        Assert.Contains(conflicting.SiloAddress.ToString(), error.Message);
        var after = await table.ReadAllAsync(token);
        Assert.Equal(before.Version.Version, after.Version.Version);
        Assert.Equal(before.Version.VersionEtag, after.Version.VersionEtag);
        Assert.Equal(row.Item2, Assert.Single(after.Members).Item2);
        Assert.Equal(original.SiloAddress, Assert.Single(after.Members).Item1.SiloAddress);

        // Even a colliding caller with the other row's ETag must not change its identity.
        Assert.False(await table.UpdateRowAsync(conflicting, row.Item2, before.Version.Next(), token));
        await table.UpdateIAmAliveAsync(new MembershipEntry
        {
            SiloAddress = conflicting.SiloAddress,
            IAmAliveTime = original.IAmAliveTime.AddDays(1)
        }, token);
#pragma warning disable CS0618 // Verify point reads use the full stored address.
        Assert.Empty((await table.ReadRowAsync(conflicting.SiloAddress, token)).Members);
        var retained = Assert.Single((await table.ReadRowAsync(original.SiloAddress, token)).Members);
#pragma warning restore CS0618
        Assert.Equal(row.Item2, retained.Item2);
        Assert.Equal(original.IAmAliveTime, retained.Item1.IAmAliveTime);
        original.HostName = "updated-original";
        Assert.True(await table.UpdateRowAsync(original, row.Item2, before.Version.Next(), token));
        var collection = database.Database.GetCollection<MongoMembershipDocument>(
            strategy == MongoDBMembershipStrategy.Multiple ? "OrleansMembershipV3" : "OrleansMembershipV2");
        var document = Assert.Single(await collection.Find(FilterDefinition<MongoMembershipDocument>.Empty).ToListAsync(token));
        Assert.Equal(id, document.Id);
        Assert.Equal("updated-original", document.HostName);
        Assert.Equal(original.SiloAddress.ToParsableString(), document.SiloAddress);
    }

    private static MembershipEntry Entry(string ip) => new()
    {
        SiloAddress = SiloAddress.New(IPAddress.Parse(ip), 11111, 1),
        HostName = ip,
        SiloName = ip,
        Status = SiloStatus.Active,
        ProxyPort = 30001,
        StartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IAmAliveTime = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)
    };
}
