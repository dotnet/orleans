using System;
using System.Globalization;
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

public static class Utils
{
    public static string FormatTimestamp(DateTimeOffset ts) =>
        FormatTimestamp(Timestamp.FromDateTimeOffset(ts));

    public static string FormatTimestamp(Timestamp ts) =>
        ts.ToDateTimeOffset().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string ts) =>
        DateTimeOffset.ParseExact(ts, "O", CultureInfo.InvariantCulture);
}