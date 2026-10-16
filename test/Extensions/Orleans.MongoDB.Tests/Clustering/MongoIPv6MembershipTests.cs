using System.Net;
using MongoDB.Driver;
using Orleans.Clustering.MongoDB;
using Orleans.Providers.MongoDB.Membership.Store;
using Orleans.Runtime;
using Xunit;

namespace Orleans.MongoDB.Tests.Clustering;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Membership")]
public sealed class MongoIPv6MembershipTests
{
    [Theory]
    [InlineData(MongoDBMembershipStrategy.Multiple, "2001:db8::c000:201", "192.0.2.1")]
    [InlineData(MongoDBMembershipStrategy.Multiple, "192.0.2.1", "2001:db8::c000:201")]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated, "2001:db8::c000:201", "192.0.2.1")]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated, "192.0.2.1", "2001:db8::c000:201")]
    public async Task MixedIPv4AndIPv6_LegacyAndCanonicalIdsRemainDistinct(
        MongoDBMembershipStrategy strategy, string legacyIp, string newIp)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var table = database.CreateMembershipTable("mixed", strategy);
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var legacy = Entry(legacyIp);
        var canonical = Entry(newIp);
        var legacyId = MongoMembershipDocument.GetLegacyId("mixed", legacy.SiloAddress);
        Assert.Equal(legacyId, MongoMembershipDocument.GetLegacyId("mixed", canonical.SiloAddress));
        var collection = database.Database.GetCollection<MongoMembershipDocument>(
            strategy == MongoDBMembershipStrategy.Multiple ? "OrleansMembershipV3" : "OrleansMembershipV2");
        await collection.InsertOneAsync(MongoMembershipDocument.Create(legacy, "mixed", legacyId), cancellationToken: token);

        var initial = await table.ReadAllAsync(token);
        Assert.True(await table.InsertRowAsync(canonical, initial.Version.Next(), token));
        var inserted = await table.ReadAllAsync(token);
        Assert.Equal(2, inserted.Members.Count);
        var canonicalRow = Assert.Single(inserted.Members, row => row.Item1.SiloAddress.Equals(canonical.SiloAddress));
        var legacyRow = Assert.Single(inserted.Members, row => row.Item1.SiloAddress.Equals(legacy.SiloAddress));
        Assert.False(await table.InsertRowAsync(canonical, inserted.Version.Next(), token));
        Assert.False(await table.InsertRowAsync(legacy, inserted.Version.Next(), token));

        var heartbeat = canonical.IAmAliveTime.AddMinutes(5);
        await table.UpdateIAmAliveAsync(new MembershipEntry { SiloAddress = canonical.SiloAddress, IAmAliveTime = heartbeat }, token);
#pragma warning disable CS0618 // Verify both compatibility lookup paths select the full address.
        var canonicalRead = Assert.Single((await table.ReadRowAsync(canonical.SiloAddress, token)).Members);
        var legacyRead = Assert.Single((await table.ReadRowAsync(legacy.SiloAddress, token)).Members);
#pragma warning restore CS0618
        Assert.Equal(heartbeat, canonicalRead.Item1.IAmAliveTime);
        Assert.Equal(legacy.IAmAliveTime, legacyRead.Item1.IAmAliveTime);
        Assert.Equal(canonicalRow.Item2, canonicalRead.Item2);
        Assert.Equal(legacyRow.Item2, legacyRead.Item2);
        legacy.HostName = "updated-legacy";
        Assert.True(await table.UpdateRowAsync(legacy, legacyRead.Item2, inserted.Version.Next(), token));
        var afterLegacyUpdate = await table.ReadAllAsync(token);
        canonical.HostName = "updated-canonical";
        canonical.IAmAliveTime = heartbeat;
        Assert.True(await table.UpdateRowAsync(canonical, canonicalRead.Item2, afterLegacyUpdate.Version.Next(), token));
        var after = await table.ReadAllAsync(token);
        Assert.Equal("updated-legacy", Assert.Single(after.Members, row => row.Item1.SiloAddress.Equals(legacy.SiloAddress)).Item1.HostName);
        Assert.Equal("updated-canonical", Assert.Single(after.Members, row => row.Item1.SiloAddress.Equals(canonical.SiloAddress)).Item1.HostName);
        var documents = await collection.Find(FilterDefinition<MongoMembershipDocument>.Empty).ToListAsync(token);
        Assert.Equal(2, documents.Select(document => document.Id).Distinct().Count());
        Assert.Equal(legacyId, Assert.Single(documents, document => document.SiloAddress == legacy.SiloAddress.ToParsableString()).Id);
        var canonicalId = Assert.Single(documents, document => document.SiloAddress == canonical.SiloAddress.ToParsableString()).Id;
        Assert.StartsWith("v2:", canonicalId);
        Assert.DoesNotContain("@", canonicalId);
        Assert.NotEqual(legacyId, canonicalId);
    }

    [Theory]
    [InlineData(MongoDBMembershipStrategy.Multiple, false)]
    [InlineData(MongoDBMembershipStrategy.Multiple, true)]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated, false)]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated, true)]
    public async Task NativeIPv6WithSameLowBits_PreservesDistinctAndLegacyRows(MongoDBMembershipStrategy strategy, bool seedLegacy)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var table = database.CreateMembershipTable("ipv6", strategy);
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var first = Entry("2001:db8::c000:201");
        var second = Entry("2001:db9::c000:201");
        Assert.Equal(first.SiloAddress.Endpoint.Address.MapToIPv4(), second.SiloAddress.Endpoint.Address.MapToIPv4());
        var collection = database.Database.GetCollection<MongoMembershipDocument>(
            strategy == MongoDBMembershipStrategy.Multiple ? "OrleansMembershipV3" : "OrleansMembershipV2");
        var legacyId = MongoMembershipDocument.GetLegacyId("ipv6", first.SiloAddress);
        if (seedLegacy)
        {
            await collection.InsertOneAsync(MongoMembershipDocument.Create(first, "ipv6", legacyId), cancellationToken: token);
        }
        else
        {
            Assert.True(await table.InsertRowAsync(first, (await table.ReadAllAsync(token)).Version.Next(), token));
        }

        Assert.True(await table.InsertRowAsync(second, (await table.ReadAllAsync(token)).Version.Next(), token));
        var before = await table.ReadAllAsync(token);
        Assert.Equal(2, before.Members.Count);
        var original = Assert.Single(before.Members, row => row.Item1.SiloAddress.Equals(first.SiloAddress));
        var neighbor = Assert.Single(before.Members, row => row.Item1.SiloAddress.Equals(second.SiloAddress));
        Assert.False(await table.InsertRowAsync(first, before.Version.Next(), token));
        var heartbeat = first.IAmAliveTime.AddMinutes(5);
        await table.UpdateIAmAliveAsync(new MembershipEntry { SiloAddress = first.SiloAddress, IAmAliveTime = heartbeat }, token);
#pragma warning disable CS0618 // Verify compatibility point lookups for both stored ID formats.
        var firstRead = Assert.Single((await table.ReadRowAsync(first.SiloAddress, token)).Members);
        var secondRead = Assert.Single((await table.ReadRowAsync(second.SiloAddress, token)).Members);
#pragma warning restore CS0618
        Assert.Equal(heartbeat, firstRead.Item1.IAmAliveTime);
        Assert.Equal(second.IAmAliveTime, secondRead.Item1.IAmAliveTime);
        Assert.Equal(original.Item2, firstRead.Item2);
        Assert.Equal(neighbor.Item2, secondRead.Item2);
        first.HostName = "updated-ipv6";
        first.IAmAliveTime = heartbeat;
        Assert.True(await table.UpdateRowAsync(first, firstRead.Item2, before.Version.Next(), token));
        var after = await table.ReadAllAsync(token);
        Assert.Equal("updated-ipv6", Assert.Single(after.Members, row => row.Item1.SiloAddress.Equals(first.SiloAddress)).Item1.HostName);
        Assert.Equal(neighbor.Item2, Assert.Single(after.Members, row => row.Item1.SiloAddress.Equals(second.SiloAddress)).Item2);
        var documents = await collection.Find(FilterDefinition<MongoMembershipDocument>.Empty).ToListAsync(token);
        Assert.Equal(2, documents.Select(document => document.Id).Distinct().Count());
        Assert.Contains(second.SiloAddress.Endpoint.Address.ToString(),
            Assert.Single(documents, document => document.SiloAddress == second.SiloAddress.ToParsableString()).Id);
        var firstId = Assert.Single(documents, document => document.SiloAddress == first.SiloAddress.ToParsableString()).Id;
        if (seedLegacy) Assert.Equal(legacyId, firstId);
        else Assert.Contains(first.SiloAddress.Endpoint.Address.ToString(), firstId);
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
