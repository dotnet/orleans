using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Providers.MongoDB.Membership.Store.Single;

internal sealed class DeploymentDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string DeploymentId { get; set; } = null!;

    [BsonRequired]
    public int Version { get; set; }

    [BsonRequired]
    public string VersionEtag { get; set; } = null!;

    [BsonRequired]
    public Dictionary<string, DeploymentMembership> Members { get; set; } = null!;

    public TableVersion ToTableVersion()
    {
        return new TableVersion(Version, VersionEtag);
    }

    public MembershipTableData ToTable()
    {
        return new MembershipTableData(Members.Values.Select(x => Tuple.Create(x.ToEntry(), x.Etag)).ToList(), ToTableVersion());
    }

    public MembershipTableData ToTable(string address)
    {
        return new MembershipTableData(Members.Where(x => x.Key == address).Select(x => Tuple.Create(x.Value.ToEntry(), x.Value.Etag)).ToList(), ToTableVersion());
    }
}
