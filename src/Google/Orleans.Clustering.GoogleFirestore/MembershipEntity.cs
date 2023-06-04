using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace Orleans.Clustering.GoogleFirestore;

internal class MembershipEntity : FirestoreEntity
{
    public const string CLUSTER_GROUP = "Cluster";
    
    [FirestoreProperty("ClusterId")]
    public string ClusterId { get; set; } = default!;

    public override IDictionary<string, object?> GetFields()
    {
        return new Dictionary<string, object?>
        {
            { "ClusterId", this.ClusterId }
        };
    }
}