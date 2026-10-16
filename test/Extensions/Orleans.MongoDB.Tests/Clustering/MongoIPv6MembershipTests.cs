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
