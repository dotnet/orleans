using System.Collections.Generic;
using Google.Cloud.Firestore;


namespace Orleans.GrainDirectory.GoogleFirestore;

[FirestoreData]
public class GrainDirectoryEntity : FirestoreEntity
{
    [FirestoreProperty("SiloAddress")]
    public string SiloAddress { get; set; } = default!;

    [FirestoreProperty("ActivationId")]
    public string ActivationId { get; set; } = default!;

    [FirestoreProperty("MembershipVersion")]
    public long MembershipVersion { get; set; }

    public override IDictionary<string, object?> GetFields()
    {
        return new Dictionary<string, object?>
        {
            { "SiloAddress", this.SiloAddress },
            { "ActivationId", this.ActivationId },
            { "MembershipVersion", this.MembershipVersion }
        };
    }
}
