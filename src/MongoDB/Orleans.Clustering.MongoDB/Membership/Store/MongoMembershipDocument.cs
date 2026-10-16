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
    {
        var ip = address.Endpoint.Address;
        var host = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
        return $"{deploymentId}@{host}:{address.Endpoint.Port}/{address.Generation}";
    }

    internal static string GetLegacyId(string deploymentId, Runtime.SiloAddress address)
        => $"{deploymentId}@{address.Endpoint.Address.MapToIPv4()}:{address.Endpoint.Port}/{address.Generation}";

    internal static FilterDefinition<MongoMembershipDocument> IdentityFilter(string deploymentId, Runtime.SiloAddress address)
    {
        var filter = Builders<MongoMembershipDocument>.Filter;
        return filter.And(
            filter.In(row => row.Id, new[] { GetId(deploymentId, address), GetLegacyId(deploymentId, address) }),
            filter.Eq(row => row.DeploymentId, deploymentId),
            filter.Eq(row => row.SiloAddress, address.ToParsableString()));
    }

    public static MongoMembershipDocument Create(MembershipEntry entry, string deploymentId, string id)
    {
        var result = Create<MongoMembershipDocument>(entry);

        result.Id = id;
        result.DeploymentId = deploymentId;

        return result;
    }
}
