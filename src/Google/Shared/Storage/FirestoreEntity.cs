using System.Collections.Generic;
using Google.Cloud.Firestore;

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.Firestore;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.Firestore;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.Firestore;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.Firestore;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

internal abstract class FirestoreEntity
{
    [FirestoreDocumentId]
    public string Id { get; set; } = default!;

    [FirestoreDocumentUpdateTimestamp]
    public Timestamp? ETag { get; set; }

    public abstract IDictionary<string, object?> GetFields();
}