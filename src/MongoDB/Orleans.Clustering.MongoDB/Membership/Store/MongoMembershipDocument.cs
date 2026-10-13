using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Orleans.Providers.MongoDB.Membership.Store;

internal sealed class MongoMembershipDocument : MembershipBase
{
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = null!;

    [BsonRequired]
    public string DeploymentId { get; set; } = null!;

    public static MongoMembershipDocument Create(MembershipEntry entry, string deploymentId, string id)
    {
        var result = Create<MongoMembershipDocument>(entry);

        result.Id = id;
        result.DeploymentId = deploymentId;

        return result;
    }
}
