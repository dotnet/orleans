using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Utilities
{
    internal static class SymbolDisplayFormatExtensions
    {
        public static SymbolDisplayFormat WithTypeQualificationStyle(this SymbolDisplayFormat format, SymbolDisplayTypeQualificationStyle style)
        {
            return new SymbolDisplayFormat(
                format.GlobalNamespaceStyle,
                style,
                format.GenericsOptions,
                format.MemberOptions,
                format.DelegateStyle,
                format.ExtensionMethodStyle,
                format.ParameterOptions,
                format.PropertyStyle,
                format.LocalOptions,
                format.KindOptions,
                format.MiscellaneousOptions);
        }
    }
}