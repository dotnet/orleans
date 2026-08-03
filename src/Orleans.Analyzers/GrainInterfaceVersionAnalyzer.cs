#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Orleans.Analyzers;

/// <summary>
/// An analyzer that tracks grain interface definitions and their versions.
/// It ensures that interface changes are accompanied by version increments.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class GrainInterfaceVersionAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId0016 = "ORLEANS0016";
    public const string RuleId0017 = "ORLEANS0017";
    public const string RuleId0018 = "ORLEANS0018";
    public const string RuleId0019 = "ORLEANS0019";
    public const string RuleId0020 = "ORLEANS0020";
    public const string RuleId0021 = "ORLEANS0021";

    // Property bag keys for code fixes
    internal const string InterfaceNamePropertyKey = "InterfaceName";
    internal const string MemberNamePropertyKey = "MemberName";
    internal const string ExpectedVersionPropertyKey = "ExpectedVersion";
    internal const string ActualVersionPropertyKey = "ActualVersion";
    internal const string ExpectedSignaturePropertyKey = "ExpectedSignature";
    internal const string ActualSignaturePropertyKey = "ActualSignature";

    internal const string RetiredPrefix = "*RETIRED*";

    private static readonly DiagnosticDescriptor InterfaceNotDeclaredRule = new(
        id: RuleId0016,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceNotDeclaredTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceNotDeclaredMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceNotDeclaredDescription), Resources.ResourceManager, typeof(Resources)));

    private static readonly DiagnosticDescriptor InterfaceVersionMismatchRule = new(
        id: RuleId0017,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceVersionMismatchTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceVersionMismatchMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceVersionMismatchDescription), Resources.ResourceManager, typeof(Resources)));

    private static readonly DiagnosticDescriptor MemberNotDeclaredRule = new(
        id: RuleId0018,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceMemberNotDeclaredTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceMemberNotDeclaredMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceMemberNotDeclaredDescription), Resources.ResourceManager, typeof(Resources)));

    private static readonly DiagnosticDescriptor RemovedInterfaceNotRetiredRule = new(
        id: RuleId0019,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceRemovedNotRetiredTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceRemovedNotRetiredMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceRemovedNotRetiredDescription), Resources.ResourceManager, typeof(Resources)),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor GrainInterfacesFileMissingRule = new(
        id: RuleId0020,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfacesFileMissingTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfacesFileMissingMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfacesFileMissingDescription), Resources.ResourceManager, typeof(Resources)),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    internal static readonly DiagnosticDescriptor DuplicateInterfaceDeclarationRule = new(
        id: RuleId0021,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceDuplicateDeclarationTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceDuplicateDeclarationMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceDuplicateDeclarationDescription), Resources.ResourceManager, typeof(Resources)));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            InterfaceNotDeclaredRule,
            InterfaceVersionMismatchRule,
            MemberNotDeclaredRule,
            RemovedInterfaceNotRetiredRule,
            GrainInterfacesFileMissingRule,
            DuplicateInterfaceDeclarationRule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        // Try to find the GrainInterfaces.txt file
        var grainInterfacesFile = context.Options.AdditionalFiles
            .FirstOrDefault(f => Path.GetFileName(f.Path).Equals(Constants.GrainInterfacesFileName, StringComparison.OrdinalIgnoreCase));

        GrainInterfaceData? data = null;
        List<Diagnostic>? fileParseErrors = null;

        if (grainInterfacesFile is not null)
        {
            var sourceText = grainInterfacesFile.GetText(context.CancellationToken);
            if (sourceText is not null)
            {
                (data, fileParseErrors) = GrainInterfaceFileParser.Parse(sourceText, grainInterfacesFile.Path);
            }
        }

        var impl = new Impl(context.Compilation, data, grainInterfacesFile, fileParseErrors);

        context.RegisterSymbolAction(impl.AnalyzeNamedType, SymbolKind.NamedType);
        context.RegisterCompilationEndAction(impl.OnCompilationEnd);
    }

    private sealed class Impl
    {
        private readonly Compilation _compilation;
        private readonly GrainInterfaceData? _data;
        private readonly AdditionalText? _grainInterfacesFile;
        private readonly List<Diagnostic>? _fileParseErrors;
        private readonly ConcurrentDictionary<string, bool> _visitedInterfaces = new(StringComparer.Ordinal);
        private readonly INamedTypeSymbol? _iGrainType;
        private readonly INamedTypeSymbol? _aliasAttributeType;
        private readonly INamedTypeSymbol? _versionAttributeType;

        public Impl(
            Compilation compilation,
            GrainInterfaceData? data,
            AdditionalText? grainInterfacesFile,
            List<Diagnostic>? fileParseErrors)
        {
            _compilation = compilation;
            _data = data;
            _grainInterfacesFile = grainInterfacesFile;
            _fileParseErrors = fileParseErrors;

            _iGrainType = compilation.GetTypeByMetadataName(Constants.IGrainFullyQualifiedName);
            _aliasAttributeType = compilation.GetTypeByMetadataName(Constants.AliasAttributeFullyQualifiedName);
            _versionAttributeType = compilation.GetTypeByMetadataName(Constants.VersionAttributeFullyQualifiedName);
        }

        public void AnalyzeNamedType(SymbolAnalysisContext context)
        {
            var namedType = (INamedTypeSymbol)context.Symbol;

            // Only analyze interfaces
            if (namedType.TypeKind != TypeKind.Interface)
            {
                return;
            }

            // Check if this is a grain interface (extends IGrain)
            if (!IsGrainInterface(namedType))
            {
                return;
            }

            // Skip IGrain itself and its base interfaces
            if (IsBaseGrainInterface(namedType))
            {
                return;
            }

            var interfaceName = namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "");

            _visitedInterfaces.TryAdd(interfaceName, true);

            // If no file exists, report that interfaces are not being tracked
            if (_data is null)
            {
                // We'll report file missing at compilation end
                return;
            }

            // Check if interface is declared in the file
            if (!_data.Interfaces.TryGetValue(interfaceName, out var declaredInterface))
            {
                // Interface not found in file
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add(InterfaceNamePropertyKey, interfaceName);

                foreach (var location in namedType.Locations.Where(l => l.IsInSource))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InterfaceNotDeclaredRule,
                        location,
                        properties,
                        interfaceName));
                }
                return;
            }

            // Check if retired
            if (declaredInterface.IsRetired)
            {
                // Interface exists in code but is marked as retired in file
                // This could be a diagnostic, but for now we treat it as a mismatch
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add(InterfaceNamePropertyKey, interfaceName);

                foreach (var location in namedType.Locations.Where(l => l.IsInSource))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InterfaceNotDeclaredRule,
                        location,
                        properties,
                        interfaceName));
                }
                return;
            }

            // Check version attribute matches
            var codeVersion = GetVersionFromAttribute(namedType);
            if (codeVersion != declaredInterface.Version)
            {
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add(InterfaceNamePropertyKey, interfaceName)
                    .Add(ExpectedVersionPropertyKey, declaredInterface.Version.ToString())
                    .Add(ActualVersionPropertyKey, codeVersion.ToString());

                foreach (var location in namedType.Locations.Where(l => l.IsInSource))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InterfaceVersionMismatchRule,
                        location,
                        properties,
                        interfaceName,
                        declaredInterface.Version,
                        codeVersion));
                }
            }

            // Check alias matches
            var codeAlias = GetAliasFromAttribute(namedType);
            if (!string.Equals(codeAlias, declaredInterface.Alias, StringComparison.Ordinal))
            {
                // Alias mismatch - could add a separate diagnostic for this
            }

            // Check members
            foreach (var member in namedType.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.MethodKind != MethodKind.Ordinary)
                {
                    continue;
                }

                var memberSignature = GetMethodSignature(member);
                var memberAlias = GetAliasFromAttribute(member);

                if (!declaredInterface.Members.TryGetValue(memberSignature, out var declaredMember))
                {
                    // Member not found - interface has changed
                    var properties = ImmutableDictionary<string, string?>.Empty
                        .Add(InterfaceNamePropertyKey, interfaceName)
                        .Add(MemberNamePropertyKey, memberSignature);

                    foreach (var location in member.Locations.Where(l => l.IsInSource))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            MemberNotDeclaredRule,
                            location,
                            properties,
                            memberSignature,
                            interfaceName));
                    }
                }
            }
        }

        public void OnCompilationEnd(CompilationAnalysisContext context)
        {
            // Report file parse errors
            if (_fileParseErrors is not null)
            {
                foreach (var error in _fileParseErrors)
                {
                    context.ReportDiagnostic(error);
                }
            }

            // Report file missing if any grain interfaces were found but no file exists
            if (_data is null && _visitedInterfaces.Count > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    GrainInterfacesFileMissingRule,
                    Location.None,
                    Constants.GrainInterfacesFileName));
            }

            // Check for removed interfaces (in file but not in code)
            if (_data is not null && _grainInterfacesFile is not null)
            {
                var sourceText = _grainInterfacesFile.GetText(context.CancellationToken);
                if (sourceText is not null)
                {
                    foreach (var kvp in _data.Interfaces)
                    {
                        if (kvp.Value.IsRetired)
                        {
                            continue;
                        }

                        if (!_visitedInterfaces.ContainsKey(kvp.Key))
                        {
                            // Interface in file but not in code - needs to be retired
                            var location = kvp.Value.GetLocation(sourceText, _grainInterfacesFile.Path);

                            var properties = ImmutableDictionary<string, string?>.Empty
                                .Add(InterfaceNamePropertyKey, kvp.Key);

                            context.ReportDiagnostic(Diagnostic.Create(
                                RemovedInterfaceNotRetiredRule,
                                location,
                                properties,
                                kvp.Key));
                        }
                    }
                }
            }
        }

        private bool IsGrainInterface(INamedTypeSymbol type)
        {
            if (_iGrainType is null)
            {
                return false;
            }

            return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _iGrainType))
                   || SymbolEqualityComparer.Default.Equals(type, _iGrainType);
        }

        private static bool IsBaseGrainInterface(INamedTypeSymbol type)
        {
            var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return fullName.StartsWith("global::Orleans.IGrain", StringComparison.Ordinal)
                   || fullName.StartsWith("global::Orleans.Runtime.IAddressable", StringComparison.Ordinal);
        }

        private ushort GetVersionFromAttribute(INamedTypeSymbol type)
        {
            if (_versionAttributeType is null)
            {
                return 0;
            }

            foreach (var attribute in type.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _versionAttributeType))
                {
                    if (attribute.ConstructorArguments.Length > 0 &&
                        attribute.ConstructorArguments[0].Value is ushort version)
                    {
                        return version;
                    }
                }
            }

            return 0;
        }

        private string? GetAliasFromAttribute(ISymbol symbol)
        {
            if (_aliasAttributeType is null)
            {
                return null;
            }

            foreach (var attribute in symbol.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _aliasAttributeType))
                {
                    if (attribute.ConstructorArguments.Length > 0 &&
                        attribute.ConstructorArguments[0].Value is string alias)
                    {
                        return alias;
                    }
                }
            }

            return null;
        }

        private static string GetMethodSignature(IMethodSymbol method)
        {
            var sb = new StringBuilder();
            sb.Append(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""));
            sb.Append('.');
            sb.Append(method.Name);
            sb.Append('(');

            for (int i = 0; i < method.Parameters.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var param = method.Parameters[i];
                sb.Append(param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                sb.Append(' ');
                sb.Append(param.Name);
            }

            sb.Append(')');
            sb.Append(" -> ");
            sb.Append(method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

            return sb.ToString();
        }
    }
}

/// <summary>
/// Represents the parsed data from a GrainInterfaces.txt file.
/// </summary>
internal sealed class GrainInterfaceData
{
    public Dictionary<string, DeclaredGrainInterface> Interfaces { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Represents a declared grain interface in the GrainInterfaces.txt file.
/// </summary>
internal sealed class DeclaredGrainInterface
{
    public DeclaredGrainInterface(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public string? Alias { get; set; }
    public ushort Version { get; set; }
    public bool IsRetired { get; set; }
    public TextSpan Span { get; set; }
    public Dictionary<string, DeclaredGrainMember> Members { get; } = new Dictionary<string, DeclaredGrainMember>(StringComparer.Ordinal);

    public Location GetLocation(SourceText sourceText, string filePath)
    {
        var lineSpan = sourceText.Lines.GetLinePositionSpan(Span);
        return Location.Create(filePath, Span, lineSpan);
    }
}

/// <summary>
/// Represents a declared grain interface member in the GrainInterfaces.txt file.
/// </summary>
internal sealed class DeclaredGrainMember
{
    public DeclaredGrainMember(string signature)
    {
        Signature = signature;
    }

    public string Signature { get; }
    public string? Alias { get; set; }
    public TextSpan Span { get; set; }
}

/// <summary>
/// Parses GrainInterfaces.txt files.
/// </summary>
internal static class GrainInterfaceFileParser
{
    // Regex patterns for parsing
    // Interface line: [Alias("x")] Namespace.IInterface<T> [Version(N)]
    // Or with retired: *RETIRED* [Alias("x")] Namespace.IInterface<T> [Version(N)]
    // The name can include generic type parameters like IMyGrain<T> or IMyGrain<TKey, TValue>
    private static readonly Regex InterfacePattern = new(
        @"^(?<retired>\*RETIRED\*\s*)?(\[Alias\(""(?<alias>[^""]+)""\)\]\s*)?(?<name>[\w.]+(?:<[\w,\s]+>)?)\s*\[Version\((?<version>\d+)\)\]$",
        RegexOptions.Compiled);

    // Member line: [Alias("x")] Namespace.IInterface<T>.Method(params) -> ReturnType
    // The signature includes the full interface name (possibly generic) and method
    private static readonly Regex MemberPattern = new(
        @"^(\[Alias\(""(?<alias>[^""]+)""\)\]\s*)?(?<signature>[\w.]+(?:<[\w,\s]+>)?\.[^(]+\([^)]*\)\s*->\s*.+)$",
        RegexOptions.Compiled);

    public static (GrainInterfaceData Data, List<Diagnostic>? Errors) Parse(SourceText sourceText, string filePath)
    {
        var data = new GrainInterfaceData();
        List<Diagnostic>? errors = null;
        DeclaredGrainInterface? currentInterface = null;

        foreach (var textLine in sourceText.Lines)
        {
            var lineText = textLine.ToString().Trim();

            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(lineText) || lineText.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            // Try to match interface declaration
            var interfaceMatch = InterfacePattern.Match(lineText);
            if (interfaceMatch.Success)
            {
                var name = interfaceMatch.Groups["name"].Value;
                var alias = interfaceMatch.Groups["alias"].Success ? interfaceMatch.Groups["alias"].Value : null;
                var version = ushort.Parse(interfaceMatch.Groups["version"].Value);
                var isRetired = interfaceMatch.Groups["retired"].Success;

                if (data.Interfaces.ContainsKey(name))
                {
                    // Duplicate declaration
                    errors ??= new List<Diagnostic>();
                    var location = Location.Create(
                        filePath,
                        textLine.Span,
                        sourceText.Lines.GetLinePositionSpan(textLine.Span));
                    errors.Add(Diagnostic.Create(
                        GrainInterfaceVersionAnalyzer.DuplicateInterfaceDeclarationRule,
                        location,
                        name));
                    continue;
                }

                currentInterface = new DeclaredGrainInterface(name)
                {
                    Alias = alias,
                    Version = version,
                    IsRetired = isRetired,
                    Span = textLine.Span
                };
                data.Interfaces[name] = currentInterface;
                continue;
            }

            // Try to match member declaration
            var memberMatch = MemberPattern.Match(lineText);
            if (memberMatch.Success && currentInterface is not null)
            {
                var signature = memberMatch.Groups["signature"].Value;
                var alias = memberMatch.Groups["alias"].Success ? memberMatch.Groups["alias"].Value : null;

                // Extract interface name from signature to verify it belongs to current interface
                // Signature format: Namespace.IInterface<T>.Method(params) -> ReturnType
                // We need to find the last '.' before the '(' to get the method separator
                var parenIndex = signature.IndexOf('(');
                if (parenIndex > 0)
                {
                    var methodPartBeforeParen = signature.Substring(0, parenIndex);
                    var dotIndex = methodPartBeforeParen.LastIndexOf('.');
                    if (dotIndex > 0)
                    {
                        var memberInterfaceName = signature.Substring(0, dotIndex);
                        if (memberInterfaceName == currentInterface.Name)
                        {
                            currentInterface.Members[signature] = new DeclaredGrainMember(signature)
                            {
                                Alias = alias,
                                Span = textLine.Span
                            };
                        }
                    }
                }
            }
        }

        return (data, errors);
    }
}
