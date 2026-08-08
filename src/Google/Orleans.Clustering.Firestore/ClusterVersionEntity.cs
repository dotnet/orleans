using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace Orleans.Clustering.Firestore;

[FirestoreData]
internal class ClusterVersionEntity : FirestoreEntity
{
    [FirestoreProperty("MembershipVersion")]
    public int MembershipVersion { get; set; }

    public override IDictionary<string, object?> GetFields() => new Dictionary<string, object?>
    {
        ["MembershipVersion"] = this.MembershipVersion,
    };

    public TableVersion ToTableVersion() => new(this.MembershipVersion, Utils.FormatTimestamp(this.ETag!.Value));
}