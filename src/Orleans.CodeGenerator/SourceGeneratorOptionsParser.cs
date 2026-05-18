using System.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

internal static class SourceGeneratorOptionsParser
{
    private static int _debuggerLaunchState;

    internal static CodeGeneratorOptions CreateCodeGeneratorOptions(SourceGeneratorOptions options)
    {
        return new CodeGeneratorOptions
        {
            GenerateFieldIds = options.GenerateFieldIds,
            GenerateCompatibilityInvokers = options.GenerateCompatibilityInvokers,
        };
    }

    internal static void AttachDebuggerIfRequested(SourceGeneratorOptions options)
    {
        if (!options.AttachDebugger || Debugger.IsAttached)
        {
            return;
        }

        if (Interlocked.Exchange(ref _debuggerLaunchState, 1) == 0)
        {
            Debugger.Launch();
        }
    }

    internal static SourceGeneratorOptions ParseOptions(AnalyzerConfigOptions globalOptions)
    {
        var result = new SourceGeneratorOptions();

        if (globalOptions.TryGetValue("build_property.orleans_attachdebugger", out var attachDebuggerOption)
            && string.Equals("true", attachDebuggerOption, StringComparison.OrdinalIgnoreCase))
        {
            result.AttachDebugger = true;
        }

        if (globalOptions.TryGetValue("build_property.orleans_generatefieldids", out var generateFieldIds) && generateFieldIds is { Length: > 0 }
            && Enum.TryParse(generateFieldIds, out GenerateFieldIds fieldIdOption))
        {
            result.GenerateFieldIds = fieldIdOption;
        }

        if (globalOptions.TryGetValue("build_property.orleansgeneratecompatibilityinvokers", out var generateCompatInvokersValue)
            && bool.TryParse(generateCompatInvokersValue, out var genCompatInvokers))
        {
            result.GenerateCompatibilityInvokers = genCompatInvokers;
        }

        return result;
    }

}

internal struct SourceGeneratorOptions : IEquatable<SourceGeneratorOptions>
{
    public GenerateFieldIds GenerateFieldIds { get; set; }
    public bool GenerateCompatibilityInvokers { get; set; }
    public bool AttachDebugger { get; set; }

    public readonly bool Equals(SourceGeneratorOptions other)
        => GenerateFieldIds == other.GenerateFieldIds
            && GenerateCompatibilityInvokers == other.GenerateCompatibilityInvokers
            && AttachDebugger == other.AttachDebugger;

    public override readonly bool Equals(object obj) => obj is SourceGeneratorOptions other && Equals(other);

    public override readonly int GetHashCode()
    {
        unchecked
        {
            var hash = (int)GenerateFieldIds;
            hash = hash * 31 + (GenerateCompatibilityInvokers ? 1 : 0);
            hash = hash * 31 + (AttachDebugger ? 1 : 0);
            return hash;
        }
    }
}
