using System.Collections.Generic;
using Google.Cloud.Firestore;

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.GoogleFirestore;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.GoogleFirestore;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.GoogleFirestore;
#elif GOOGLE_TESTS
namespace Orleans.Tests.GoogleFirestore;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.GoogleFirestore;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

public abstract class FirestoreEntity
{
    [FirestoreDocumentId]
    public string Id { get; set; } = default!;

    [FirestoreDocumentUpdateTimestamp]
    public Timestamp? ETag { get; set; }

    public abstract IDictionary<string, object?> GetFields();
}