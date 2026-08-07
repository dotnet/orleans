using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Google.Cloud.Firestore;
using Orleans.Runtime;

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.GoogleFirestore;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.GoogleFirestore;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.GoogleFirestore;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.GoogleFirestore;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

internal static partial class Utils
{
    private const string ENCODED_ID_PREFIX = "id-";

    internal static string FormatTimestamp(Timestamp ts)
    {
        var proto = ts.ToProto();
        return FormattableString.Invariant($"{proto.Seconds}.{proto.Nanos}");
    }

    internal static Timestamp ParseTimestamp(string ts)
    {
        var parts = ts.Split('.');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
        {
            throw new FormatException("The value is not a valid Firestore ETag.");
        }

        try
        {
            return Timestamp.FromProto(new()
            {
                Seconds = seconds,
                Nanos = nanos,
            });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new FormatException("The value is not a valid Firestore ETag.", exception);
        }
    }

    internal static string SanitizeGrainId(GrainId grainId) => SanitizeId(grainId.ToString());

    internal static string SanitizeId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return ENCODED_ID_PREFIX + Convert.ToBase64String(Encoding.UTF8.GetBytes(id))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static string ParseId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!id.StartsWith(ENCODED_ID_PREFIX, StringComparison.Ordinal))
        {
            throw new FormatException("The value is not a valid encoded Firestore identifier.");
        }

        var encoded = id[ENCODED_ID_PREFIX.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    internal static GrainId ParseGrainId(string grainId) => GrainId.Parse(ParseId(grainId));

    [GeneratedRegex("__.*__", RegexOptions.CultureInvariant)]
    internal static partial Regex ForbiddenIdRegex();
}