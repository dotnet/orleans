namespace Orleans.Dashboard.Metrics.TypeFormatting;

internal enum ParseState
{
    TypeNameSection,
    GenericCount,
    GenericArray,
    TypeArray
}
