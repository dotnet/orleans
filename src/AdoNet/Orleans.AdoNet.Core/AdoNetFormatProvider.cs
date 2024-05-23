namespace Orleans.AdoNet.Core;

/// <summary>
/// Formats .NET types appropriately for database consumption in non-parameterized queries.
/// </summary>
internal class AdoNetFormatProvider : IFormatProvider
{
    private readonly AdoNetFormatter formatter = new AdoNetFormatter();

    /// <summary>
    /// Returns an instance of the formatter
    /// </summary>
    /// <param name="formatType">Requested format type</param>
    /// <returns></returns>
    public object GetFormat(Type formatType)
    {
        return formatType == typeof(ICustomFormatter) ? formatter : null;
    }

    private class AdoNetFormatter : ICustomFormatter
    {
        public string Format(string format, object arg, IFormatProvider formatProvider)
        {
            //This null check applies also to Nullable<T> when T does not have value defined.
            if (arg == null)
            {
                return "NULL";
            }

            if (arg is string)
            {
                return "N'" + ((string)arg).Replace("'", "''", StringComparison.Ordinal) + "'";
            }

            if (arg is DateTime)
            {
                return "'" + ((DateTime)arg).ToString("O") + "'";
            }

            if (arg is DateTimeOffset)
            {
                return "'" + ((DateTimeOffset)arg).ToString("O") + "'";
            }

            if (arg is IFormattable)
            {
                return ((IFormattable)arg).ToString(format, CultureInfo.InvariantCulture);
            }

            return arg.ToString();
        }
    }
}
