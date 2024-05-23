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
            return arg switch
            {
                null => "NULL",
                string v => "N'" + v.Replace("'", "''", StringComparison.Ordinal) + "'",
                DateTime time => "'" + time.ToString("O") + "'",
                DateTimeOffset offset => "'" + offset.ToString("O") + "'",
                IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
                _ => arg.ToString()!
            };
        }
    }
}
