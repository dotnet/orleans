using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Google.Cloud.Firestore;
using Orleans.Runtime;

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

public static partial class Utils
{
    private const string PERCENTAGE_SYMBOL = "%";
    private const string PERCENTAGE_SYMBOL_ENCODED = "%25";
    private const string FORWARD_SLASH_SYMBOL = "/";
    private const string FORWARD_SLASH_SYMBOL_ENCODED = "%2F";

    public static string FormatDateTime(DateTimeOffset dto) => dto.ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset ParseDateTime(string dto) => DateTimeOffset.ParseExact(dto, "O", CultureInfo.InvariantCulture);

    public static string FormatTimestamp(Timestamp ts)
    {
        var proto = ts.ToProto();
        return $"{proto.Seconds}.{proto.Nanos}";
    }

    public static Timestamp ParseTimestamp(string ts)
    {
        var parts = ts.Split('.');
        return Timestamp.FromProto(new()
        {
            Seconds = long.Parse(parts[0]),
            Nanos = int.Parse(parts[1])
        });
    }

    public static string SanitizeGrainId(GrainId grainId) => SanitizeId(grainId.ToString());
    public static string SanitizeId(string id)
    {
        return ForbiddenIdRegex().IsMatch(id)
            ? throw new OrleansException($"Id {id} contains forbidden characters")
            : id.Replace(PERCENTAGE_SYMBOL, PERCENTAGE_SYMBOL_ENCODED).Replace(FORWARD_SLASH_SYMBOL, FORWARD_SLASH_SYMBOL_ENCODED);
    }

    public static GrainId ParseGrainId(string grainId) =>
        GrainId.Parse(grainId.Replace(PERCENTAGE_SYMBOL_ENCODED, PERCENTAGE_SYMBOL).Replace(FORWARD_SLASH_SYMBOL_ENCODED, FORWARD_SLASH_SYMBOL));

    [GeneratedRegex("__.*__", RegexOptions.CultureInvariant)]
    internal static partial Regex ForbiddenIdRegex();
}