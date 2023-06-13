using System;
using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace Orleans.Reminders.GoogleFirestore;

[FirestoreData]
public class ReminderEntity : FirestoreEntity
{
    [FirestoreProperty("Name")]
    public string Name { get; set; } = default!;

    [FirestoreProperty("StartAt")]
    public DateTimeOffset StartAt { get; set; }

    [FirestoreProperty("Period")]
    public long Period { get; set; }

    [FirestoreProperty("GrainHash")]
    public uint GrainHash { get; set; }

    [FirestoreProperty("GrainId")]
    public string GrainId { get; set; } = default!;

    public override IDictionary<string, object?> GetFields()
    {
        return new Dictionary<string, object?>
        {
            { "Name", Name },
            { "StartAt", StartAt },
            { "Period", Period },
            { "GrainHash", GrainHash },
            { "GrainId", GrainId },
        };
    }
}
