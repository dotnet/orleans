using System.Text;
using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace Orleans.Clustering.GoogleFirestore;

[FirestoreData]
internal class ClusterVersionEntity : MembershipEntity
{
    [FirestoreProperty("MembershipVersion")]
    public int MembershipVersion { get; set; }

    public override IDictionary<string, object?> GetFields()
    {
        var fields = base.GetFields();
        fields.Add("MembershipVersion", this.MembershipVersion);
        return fields;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("VersionRow [");
        sb.Append(" Deployment=").Append(this.ClusterId);
        sb.Append(" MembershipVersion=").Append(this.MembershipVersion);
        sb.Append(']');

        return sb.ToString();
    }
}