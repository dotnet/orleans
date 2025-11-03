using System;
using System.Globalization;
using Orleans.Runtime;

namespace Orleans.Dashboard.Core;

internal static class Extensions
{
    private static readonly DateTime UnixStart = new(1970, 1, 1);

    public static string PrimaryKeyAsString(this GrainReference grainRef)
    {
        if (grainRef.IsPrimaryKeyBasedOnLong()) // Long
        {
            var longKey = grainRef.GetPrimaryKeyLong(out var longExt);

            return longExt != null ? $"{longKey} + {longExt}" : longKey.ToString();
        }

        var stringKey = grainRef.GetPrimaryKeyString();

        if (stringKey == null) // Guid
        {
            var guidKey = grainRef.GetPrimaryKey(out var guidExt).ToString();

            return guidExt != null ? $"{guidKey} + {guidExt}" : guidKey;
        }

        return stringKey;
    }

    public static string ToPeriodString(this DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss");

    public static long ToPeriodNumber(this DateTime value) => (long)value.Subtract(UnixStart).TotalSeconds;

    public static string ToISOString(this DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
