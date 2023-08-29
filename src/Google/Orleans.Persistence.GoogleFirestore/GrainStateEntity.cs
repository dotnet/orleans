using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace Orleans.Persistence.GoogleFirestore;

[FirestoreData]
public class GrainStateEntity : FirestoreEntity
{
    [FirestoreProperty("Name")]
    public string Name { get; set; } = default!;

    [FirestoreProperty("Payload")]
    public byte[]? Payload { get; set; }

    public override IDictionary<string, object?> GetFields()
    {
        var fields = new Dictionary<string, object?>
        {
            { "Name", this.Name }
        };

        if (this.Payload is not null)
        {
            fields.Add("Payload", this.Payload);
        }

        return fields;
    }

}
