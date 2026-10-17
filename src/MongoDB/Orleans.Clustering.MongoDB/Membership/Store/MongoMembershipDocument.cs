using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Orleans.Providers.MongoDB.Membership.Store;

internal sealed class MongoMembershipDocument : MembershipBase
{
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = null!;

    [BsonRequired]
    public string DeploymentId { get; set; } = null!;

    internal static string GetId(string deploymentId, Runtime.SiloAddress address)
        => $"{deploymentId}@{address.Endpoint.Address.MapToIPv4()}:{address.Endpoint.Port}/{address.Generation}";

    internal static FilterDefinition<MongoMembershipDocument> IdentityFilter(string deploymentId, Runtime.SiloAddress address)
    {
        var filter = Builders<MongoMembershipDocument>.Filter;
        return filter.And(
            filter.Eq(row => row.Id, GetId(deploymentId, address)),
            filter.Eq(row => row.DeploymentId, deploymentId),
            filter.Eq(row => row.SiloAddress, address.ToParsableString()));
    }

    internal static async Task ThrowIfIdentityCollision(
        IMongoCollection<MongoMembershipDocument> collection,
        string deploymentId,
        Runtime.SiloAddress address,
        CancellationToken cancellationToken)
    {
        var id = GetId(deploymentId, address);
        var existing = await collection.WithReadPreference(ReadPreference.Primary).Find(row => row.Id == id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null
            && (existing.DeploymentId != deploymentId || existing.SiloAddress != address.ToParsableString()))
        {
            throw new InvalidOperationException(
                $"MongoDB membership key collision for silo '{address}'. The legacy key is already used by silo '{existing.SiloAddress}' in cluster '{existing.DeploymentId}'.");
        }
    }

    public static MongoMembershipDocument Create(MembershipEntry entry, string deploymentId, string id)
    {
        var result = Create<MongoMembershipDocument>(entry);

        result.Id = id;
        result.DeploymentId = deploymentId;

        return result;
    }
}
