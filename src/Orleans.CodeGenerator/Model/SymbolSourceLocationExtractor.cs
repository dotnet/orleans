using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

internal static class SymbolSourceLocationExtractor
{
    internal static SourceLocationModel GetSourceLocation(ISymbol? symbol)
    {
        var sourceLocation = symbol?.Locations.FirstOrDefault(static location => location.IsInSource);
        return sourceLocation is null
            ? new SourceLocationModel(sourceOrderGroup: 1, filePath: string.Empty, position: int.MaxValue)
            : new SourceLocationModel(
                sourceOrderGroup: 0,
                filePath: sourceLocation.SourceTree?.FilePath ?? string.Empty,
                position: sourceLocation.SourceSpan.Start);
    }
}
