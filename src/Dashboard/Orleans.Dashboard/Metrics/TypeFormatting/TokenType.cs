namespace Orleans.Dashboard.Metrics.TypeFormatting;

internal enum TokenType
{
    TypeNameSection,
    GenericCount,
    GenericArrayStart,
    GenericArrayEnd,
    TypeArrayStart,
    TypeArrayEnd,
    GenericSeparator,
    TypeSectionSeparator
}
