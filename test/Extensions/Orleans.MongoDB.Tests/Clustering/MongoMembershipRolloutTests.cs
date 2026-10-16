using System.Net;
using MongoDB.Driver;
using Orleans.Clustering.MongoDB;
using Orleans.Providers.MongoDB.Membership.Store;
using Orleans.Providers.MongoDB.Membership.Store.Multiple;
using Orleans.Runtime;
using Xunit;

namespace Orleans.MongoDB.Tests.Clustering;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Membership")]
public sealed class MongoMembershipRolloutTests
{
    [Theory]
    [InlineData(MongoDBMembershipStrategy.Multiple, false)]
    [InlineData(MongoDBMembershipStrategy.Multiple, true)]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated, false)]
    [InlineData(MongoDBMembershipStrategy.MultipleDeprecated, true)]
    public async Task UpstreamAndUpdatedWriters_ShareMembershipRowsInBothDirections(
        MongoDBMembershipStrategy strategy, bool newWriterCreates)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var table = database.CreateMembershipTable("rollout", strategy);
        await table.InitializeMembershipTableAsync(true, token);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Parse("192.0.2.1"), 11111, 1),
            HostName = "host",
            SiloName = "silo",
            ProxyPort = 30001,
            Status = SiloStatus.Active,
            StartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IAmAliveTime = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)
        };
        var collection = database.Database.GetCollection<MongoMembershipDocument>(
            strategy == MongoDBMembershipStrategy.Multiple ? "OrleansMembershipV3" : "OrleansMembershipV2");
        var legacyId = $"rollout@192.0.2.1:11111/1";
        if (newWriterCreates)
        {
            Assert.True(await table.InsertRowAsync(entry, (await table.ReadAllAsync(token)).Version.Next(), token));
        }
        else
        {
            await collection.InsertOneAsync(MongoMembershipDocument.Create(entry, "rollout", legacyId), cancellationToken: token);
            var seed = await table.ReadAllAsync(token);
            entry.HostName = "new-writer";
            Assert.True(await table.UpdateRowAsync(entry, Assert.Single(seed.Members).Item2, seed.Version.Next(), token));
        }

        var before = await table.ReadAllAsync(token);
        var observed = Assert.Single(before.Members);
        Assert.Equal(SiloStatus.Active, observed.Item1.Status);
        var beforeDocument = Assert.Single(await collection.Find(FilterDefinition<MongoMembershipDocument>.Empty).ToListAsync(token));
        Assert.Equal(legacyId, beforeDocument.Id);

        entry.Status = SiloStatus.Dead;
        entry.SuspectTimes = [Tuple.Create(entry.SiloAddress, entry.IAmAliveTime.AddMinutes(1))];
        var replacement = MongoMembershipDocument.Create(entry, "rollout", legacyId);
        // Reproduce the replacement-upsert at upstream commit 2c8d7283, including its ID selection.
        if (strategy == MongoDBMembershipStrategy.Multiple)
        {
            var versions = new TableVersionCollection(database.Client, database.Name, null, null, false);
            using var session = await database.Client.StartSessionAsync(cancellationToken: token);
            Assert.True(await session.WithTransactionAsync(async (transaction, ct) =>
            {
                Assert.True(await versions.UpsertAsync(transaction, before.Version.Next(), "rollout", ct));
                await collection.ReplaceOneAsync(transaction,
                    row => row.Id == legacyId && row.Etag == observed.Item2, replacement,
                    new ReplaceOptions { IsUpsert = true }, ct);
                return true;
            }, cancellationToken: token));
        }
        else
        {
            await collection.ReplaceOneAsync(row => row.Id == legacyId && row.Etag == observed.Item2,
                replacement, new ReplaceOptions { IsUpsert = true }, token);
        }

        var after = await table.ReadAllAsync(token);
        Assert.Single(after.Members);
        Assert.All(after.Members, row => Assert.Equal(entry.SiloAddress, row.Item1.SiloAddress));
        var dead = Assert.Single(after.Members, row => row.Item1.Status == SiloStatus.Dead);
        Assert.NotEqual(observed.Item2, dead.Item2);
        Assert.Equal(entry.IAmAliveTime.AddMinutes(1), Assert.Single(dead.Item1.SuspectTimes!).Item2);
        Assert.Equal(legacyId, Assert.Single(
            await collection.Find(FilterDefinition<MongoMembershipDocument>.Empty).ToListAsync(token)).Id);
        entry.HostName = "updated-after-old-writer";
        Assert.True(await table.UpdateRowAsync(entry, dead.Item2, after.Version.Next(), token));
        var last = Assert.Single((await table.ReadAllAsync(token)).Members);
        Assert.Equal("updated-after-old-writer", last.Item1.HostName);
        Assert.Equal(SiloStatus.Dead, last.Item1.Status);
        Assert.NotEqual(dead.Item2, last.Item2);
        Assert.Equal(legacyId, Assert.Single(
            await collection.Find(FilterDefinition<MongoMembershipDocument>.Empty).ToListAsync(token)).Id);
    }
}
