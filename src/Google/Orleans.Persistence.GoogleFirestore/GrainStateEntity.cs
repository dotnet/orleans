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

        fields.Add("Payload", this.Payload is null ? FieldValue.Delete : this.Payload);

        return fields;
    }

}
