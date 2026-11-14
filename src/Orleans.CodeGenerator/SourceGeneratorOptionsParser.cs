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
            HotReloadSafe = options.HotReload ?? false,
            SupportsUnsafeAccessors = options.SupportsUnsafeAccessors,
            SupportsGenericUnsafeAccessors = options.SupportsGenericUnsafeAccessors,
            SupportsVolatileUnsafeAccessors = options.SupportsVolatileUnsafeAccessors,
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

        if (globalOptions.TryGetValue("build_property.orleanshotreload", out var hotReloadValue)
            && bool.TryParse(hotReloadValue, out var hotReload))
        {
            result.HotReload = hotReload;
        }

        if (globalOptions.TryGetValue("build_property.TargetFrameworkIdentifier", out var frameworkIdentifier)
            && string.Equals(frameworkIdentifier, ".NETCoreApp", StringComparison.OrdinalIgnoreCase)
            && globalOptions.TryGetValue("build_property.TargetFrameworkVersion", out var frameworkVersion))
        {
            var versionText = frameworkVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? frameworkVersion.Substring(1)
                : frameworkVersion;
            if (Version.TryParse(versionText, out var version))
            {
                result.SupportsUnsafeAccessors = version.Major >= 8;
                result.SupportsGenericUnsafeAccessors = version.Major >= 9;
                // Older targets can run on .NET 9.0.0, whose field matching rejects volatile modifiers.
                result.SupportsVolatileUnsafeAccessors = version.Major >= 10;
            }
        }

        return result;
    }

}

internal struct SourceGeneratorOptions : IEquatable<SourceGeneratorOptions>
{
    public GenerateFieldIds GenerateFieldIds { get; set; }
    public bool GenerateCompatibilityInvokers { get; set; }
    public bool AttachDebugger { get; set; }

    /// <summary>
    /// Enables hot-reload-safe code generation.
    /// </summary>
    public bool? HotReload { get; set; }
    public bool SupportsUnsafeAccessors { get; set; }
    public bool SupportsGenericUnsafeAccessors { get; set; }
    public bool SupportsVolatileUnsafeAccessors { get; set; }

    public readonly bool Equals(SourceGeneratorOptions other)
        => GenerateFieldIds == other.GenerateFieldIds
            && GenerateCompatibilityInvokers == other.GenerateCompatibilityInvokers
            && AttachDebugger == other.AttachDebugger
            && HotReload == other.HotReload
            && SupportsUnsafeAccessors == other.SupportsUnsafeAccessors
            && SupportsGenericUnsafeAccessors == other.SupportsGenericUnsafeAccessors
            && SupportsVolatileUnsafeAccessors == other.SupportsVolatileUnsafeAccessors;

    public override readonly bool Equals(object obj) => obj is SourceGeneratorOptions other && Equals(other);

    public override readonly int GetHashCode()
    {
        unchecked
        {
            var hash = (int)GenerateFieldIds;
            hash = hash * 31 + (GenerateCompatibilityInvokers ? 1 : 0);
            hash = hash * 31 + (AttachDebugger ? 1 : 0);
            hash = hash * 31 + (HotReload switch { true => 1, false => 2, null => 0 });
            hash = hash * 31 + (SupportsUnsafeAccessors ? 1 : 0);
            hash = hash * 31 + (SupportsGenericUnsafeAccessors ? 1 : 0);
            hash = hash * 31 + (SupportsVolatileUnsafeAccessors ? 1 : 0);
            return hash;
        }
    }
}
