namespace Orleans.AdoNet.Core;

/// <summary>
/// Formats .NET types appropriately for database consumption in non-parameterized queries.
/// </summary>
internal class AdoNetFormatProvider : IFormatProvider
{
    private readonly AdoNetFormatter _formatter = new();

    /// <summary>
    /// Returns an instance of the formatter
    /// </summary>
    /// <param name="formatType">Requested format type</param>
    /// <returns></returns>
    public object GetFormat(Type? formatType) => formatType == typeof(ICustomFormatter) ? _formatter : null!;

    private class AdoNetFormatter : ICustomFormatter
    {
        public string Format(string? format, object? arg, IFormatProvider? formatProvider)
        {
            //This null check applies also to Nullable<T> when T does not have value defined.
            if (arg == null)
            {
                return "NULL";
            }

            if (arg is string v)
            {
                return "N'" + v.Replace("'", "''", StringComparison.Ordinal) + "'";
            }

            if (arg is DateTime time)
            {
                return "'" + time.ToString("O") + "'";
            }

            if (arg is DateTimeOffset offset)
            {
                return "'" + offset.ToString("O") + "'";
            }

            if (arg is IFormattable formattable)
            {
                return formattable.ToString(format, CultureInfo.InvariantCulture);
            }

            return arg.ToString()!;
        }
    }
}
