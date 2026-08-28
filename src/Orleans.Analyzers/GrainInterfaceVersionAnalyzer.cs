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
    public const string EnableAnalyzerPropertyName = "EnableOrleansContractsAnalyzer";
    public const string RuleId0016 = "ORLEANS0016";
    public const string RuleId0017 = "ORLEANS0017";
    public const string RuleId0018 = "ORLEANS0018";
    public const string RuleId0019 = "ORLEANS0019";
    public const string RuleId0020 = "ORLEANS0020";
    public const string RuleId0021 = "ORLEANS0021";
    public const string RuleId0022 = "ORLEANS0022";
    public const string RuleId0023 = "ORLEANS0023";
    public const string RuleId0024 = "ORLEANS0024";
    public const string RuleId0025 = "ORLEANS0025";
    public const string RuleId0027 = "ORLEANS0027";

    // Property bag keys for code fixes
    internal const string InterfaceNamePropertyKey = "InterfaceName";
    internal const string MemberNamePropertyKey = "MemberName";
    internal const string MemberAliasPropertyKey = "MemberAlias";
    internal const string MemberClrSignaturePropertyKey = "MemberClrSignature";
    internal const string ExpectedVersionPropertyKey = "ExpectedVersion";
    internal const string ActualVersionPropertyKey = "ActualVersion";
    internal const string ExpectedSignaturePropertyKey = "ExpectedSignature";
    internal const string ActualSignaturePropertyKey = "ActualSignature";
    internal const string ClassNamePropertyKey = "ClassName";
    internal const string ActualAliasPropertyKey = "ActualAlias";
    internal const string GrainInterfaceTypePropertyKey = "GrainInterfaceType";

    internal const string RetiredPrefix = "*RETIRED*";

    private static readonly DiagnosticDescriptor InterfaceNotDeclaredRule = new(
        id: RuleId0016,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceNotDeclaredTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceNotDeclaredMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceNotDeclaredDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0016));

    private static readonly DiagnosticDescriptor InterfaceVersionMismatchRule = new(
        id: RuleId0017,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceVersionMismatchTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceVersionMismatchMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceVersionMismatchDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0017));

    private static readonly DiagnosticDescriptor MemberNotDeclaredRule = new(
        id: RuleId0018,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceMemberNotDeclaredTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceMemberNotDeclaredMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceMemberNotDeclaredDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0018));

    private static readonly DiagnosticDescriptor RemovedMemberRule = new(
        id: RuleId0027,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceMemberRemovedTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceMemberRemovedMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceMemberRemovedDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0027),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor RemovedInterfaceNotRetiredRule = new(
        id: RuleId0019,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceRemovedNotRetiredTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceRemovedNotRetiredMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceRemovedNotRetiredDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0019),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor OrleansContractsFileMissingRule = new(
        id: RuleId0020,
        title: new LocalizableResourceString(nameof(Resources.OrleansContractsFileMissingTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.OrleansContractsFileMissingMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.OrleansContractsFileMissingDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0020),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    internal static readonly DiagnosticDescriptor DuplicateInterfaceDeclarationRule = new(
        id: RuleId0021,
        title: new LocalizableResourceString(nameof(Resources.GrainInterfaceDuplicateDeclarationTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainInterfaceDuplicateDeclarationMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainInterfaceDuplicateDeclarationDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0021));

    private static readonly DiagnosticDescriptor GrainClassNotDeclaredRule = new(
        id: RuleId0022,
        title: new LocalizableResourceString(nameof(Resources.GrainClassNotDeclaredTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainClassNotDeclaredMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainClassNotDeclaredDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0022));

    private static readonly DiagnosticDescriptor GrainClassAliasMismatchRule = new(
        id: RuleId0023,
        title: new LocalizableResourceString(nameof(Resources.GrainClassAliasMismatchTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainClassAliasMismatchMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainClassAliasMismatchDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0023));

    private static readonly DiagnosticDescriptor RemovedGrainClassNotRetiredRule = new(
        id: RuleId0024,
        title: new LocalizableResourceString(nameof(Resources.GrainClassRemovedNotRetiredTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainClassRemovedNotRetiredMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainClassRemovedNotRetiredDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0024),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    internal static readonly DiagnosticDescriptor DuplicateGrainClassDeclarationRule = new(
        id: RuleId0025,
        title: new LocalizableResourceString(nameof(Resources.GrainClassDuplicateDeclarationTitle), Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString(nameof(Resources.GrainClassDuplicateDeclarationMessageFormat), Resources.ResourceManager, typeof(Resources)),
        category: "Orleans.Versioning",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: new LocalizableResourceString(nameof(Resources.GrainClassDuplicateDeclarationDescription), Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId0025));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            InterfaceNotDeclaredRule,
            InterfaceVersionMismatchRule,
            MemberNotDeclaredRule,
            RemovedInterfaceNotRetiredRule,
            OrleansContractsFileMissingRule,
            DuplicateInterfaceDeclarationRule,
            GrainClassNotDeclaredRule,
            GrainClassAliasMismatchRule,
            RemovedGrainClassNotRetiredRule,
            DuplicateGrainClassDeclarationRule,
            RemovedMemberRule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                $"build_property.{EnableAnalyzerPropertyName}",
                out var enabled)
            || !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Try to find the OrleansContracts.txt file
        var grainInterfacesFile = context.Options.AdditionalFiles
            .FirstOrDefault(file =>
                Path.GetFileName(file.Path).Equals(Constants.OrleansContractsFileName, StringComparison.OrdinalIgnoreCase)
                || context.Options.AnalyzerConfigOptionsProvider.GetOptions(file)
                    .TryGetValue("build_metadata.AdditionalFiles.OrleansContractsFile", out var value)
                    && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

        GrainInterfaceData? data = null;
        SourceText? sourceText = null;
        List<Diagnostic>? fileParseErrors = null;

        if (grainInterfacesFile is not null)
        {
            sourceText = grainInterfacesFile.GetText(context.CancellationToken);
            if (sourceText is not null)
            {
                (data, fileParseErrors) = GrainInterfaceFileParser.Parse(sourceText, grainInterfacesFile.Path);
            }
        }

        var impl = new Impl(context.Compilation, data, grainInterfacesFile, sourceText, fileParseErrors);

        context.RegisterSymbolAction(impl.AnalyzeNamedType, SymbolKind.NamedType);
        context.RegisterCompilationEndAction(impl.OnCompilationEnd);
    }

    private sealed class Impl
    {
        private readonly GrainInterfaceData? _data;
        private readonly AdditionalText? _grainInterfacesFile;
        private readonly SourceText? _grainInterfacesFileText;
        private readonly List<Diagnostic>? _fileParseErrors;
        private readonly ConcurrentDictionary<string, bool> _visitedInterfaces = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _visitedClasses = new(StringComparer.Ordinal);
        private readonly ConcurrentBag<Diagnostic> _removedMemberDiagnostics = new();
        private readonly INamedTypeSymbol? _iAddressableType;
        private readonly INamedTypeSymbol? _aliasAttributeType;
        private readonly INamedTypeSymbol? _versionAttributeType;
        private Location? _firstContractLocation;

        public Impl(
            Compilation compilation,
            GrainInterfaceData? data,
            AdditionalText? grainInterfacesFile,
            SourceText? grainInterfacesFileText,
            List<Diagnostic>? fileParseErrors)
        {
            _data = data;
            _grainInterfacesFile = grainInterfacesFile;
            _grainInterfacesFileText = grainInterfacesFileText;
            _fileParseErrors = fileParseErrors;

            _iAddressableType = compilation.GetTypeByMetadataName(Constants.IAddressibleFullyQualifiedName);
            _aliasAttributeType = compilation.GetTypeByMetadataName(Constants.AliasAttributeFullyQualifiedName);
            _versionAttributeType = compilation.GetTypeByMetadataName(Constants.VersionAttributeFullyQualifiedName);
        }

        public void AnalyzeNamedType(SymbolAnalysisContext context)
        {
            var namedType = (INamedTypeSymbol)context.Symbol;

            if (namedType.TypeKind == TypeKind.Class && !namedType.IsAbstract && namedType.IsGrainClass())
            {
                AnalyzeGrainClass(context, namedType);
                return;
            }

            if (namedType.TypeKind != TypeKind.Interface)
            {
                return;
            }

            // Check if this is an RPC contract (extends IAddressable)
            if (!IsRpcContract(namedType))
            {
                return;
            }

            var interfaceName = namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "");

            // If no file exists, report that interfaces are not being tracked
            if (_data is null)
            {
                _visitedInterfaces.TryAdd(interfaceName, true);
                RecordContractLocation(namedType);
                // We'll report file missing at compilation end
                return;
            }

            // Check if interface is declared in the file
            var explicitGrainInterfaceType = GetStringAttributeValue(
                namedType,
                Constants.GrainInterfaceTypeAttributeFullyQualifiedName);
            var grainInterfaceType = GetGrainInterfaceType(namedType);
            var declaredInterface = FindDeclaredInterface(
                interfaceName,
                grainInterfaceType,
                explicitGrainInterfaceType is null
                    || string.Equals(
                        explicitGrainInterfaceType,
                        GetDefaultGrainInterfaceType(namedType),
                        StringComparison.Ordinal));
            if (declaredInterface is null)
            {
                _visitedInterfaces.TryAdd(interfaceName, true);
                // Interface not found in file
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add(InterfaceNamePropertyKey, interfaceName)
                    .Add(GrainInterfaceTypePropertyKey, grainInterfaceType);

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

            _visitedInterfaces.TryAdd(GetDeclarationKey(declaredInterface), true);

            // Check if retired
            if (declaredInterface.IsRetired)
            {
                // Interface exists in code but is marked as retired in file
                // This could be a diagnostic, but for now we treat it as a mismatch
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add(InterfaceNamePropertyKey, interfaceName)
                    .Add(GrainInterfaceTypePropertyKey, grainInterfaceType);

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
                    .Add(GrainInterfaceTypePropertyKey, grainInterfaceType)
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

            var sourceMembers = namedType.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(member => member.MethodKind == MethodKind.Ordinary && !member.IsStatic)
                .ToArray();

            // Check members
            foreach (var member in sourceMembers)
            {
                var memberSignature = GetMethodSignature(member);
                var memberAlias = GetAliasFromAttribute(member);

                if (!declaredInterface.Members.Values.Any(declaredMember =>
                    GrainInterfaceVersionAnalyzer.IsMatchingMember(
                        declaredInterface.Name,
                        declaredMember.Signature,
                        declaredMember.Alias,
                        member)))
                {
                    // Member not found - interface has changed
                    var properties = ImmutableDictionary<string, string?>.Empty
                        .Add(InterfaceNamePropertyKey, interfaceName)
                        .Add(GrainInterfaceTypePropertyKey, grainInterfaceType)
                        .Add(MemberNamePropertyKey, memberSignature)
                        .Add(MemberAliasPropertyKey, memberAlias)
                        .Add(MemberClrSignaturePropertyKey, RequiresClrComment(member) ? GetClrMethodSignature(member) : null);

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

            if (_grainInterfacesFile is not null && _grainInterfacesFileText is { } sourceText)
            {
                foreach (var declaredMember in declaredInterface.Members.Values)
                {
                    if (sourceMembers.Any(member =>
                        GrainInterfaceVersionAnalyzer.IsMatchingMember(
                            declaredInterface.Name,
                            declaredMember.Signature,
                            declaredMember.Alias,
                            member)))
                    {
                        continue;
                    }

                    var properties = ImmutableDictionary<string, string?>.Empty
                        .Add(InterfaceNamePropertyKey, interfaceName)
                        .Add(GrainInterfaceTypePropertyKey, grainInterfaceType)
                        .Add(MemberNamePropertyKey, declaredMember.Signature);
                    _removedMemberDiagnostics.Add(Diagnostic.Create(
                        RemovedMemberRule,
                        declaredMember.GetLocation(sourceText, _grainInterfacesFile.Path),
                        properties,
                        declaredMember.Signature,
                        interfaceName));
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

            foreach (var diagnostic in _removedMemberDiagnostics)
            {
                context.ReportDiagnostic(diagnostic);
            }

            // Report file missing if any grain interfaces were found but no file exists
            if (_data is null && (_visitedInterfaces.Count > 0 || _visitedClasses.Count > 0))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    OrleansContractsFileMissingRule,
                    _firstContractLocation ?? Location.None,
                    Constants.OrleansContractsFileName));
            }

            // Check for removed interfaces (in file but not in code)
            if (_data is not null && _grainInterfacesFile is not null)
            {
                if (_grainInterfacesFileText is { } sourceText)
                {
                    foreach (var declaredInterface in _data.Interfaces)
                    {
                        if (declaredInterface.IsRetired)
                        {
                            continue;
                        }

                        if (!_visitedInterfaces.ContainsKey(GetDeclarationKey(declaredInterface)))
                        {
                            // Interface in file but not in code - needs to be retired
                            var location = declaredInterface.GetLocation(sourceText, _grainInterfacesFile.Path);

                            var properties = ImmutableDictionary<string, string?>.Empty
                                .Add(InterfaceNamePropertyKey, declaredInterface.Name)
                                .Add(GrainInterfaceTypePropertyKey, declaredInterface.GrainInterfaceType);

                            context.ReportDiagnostic(Diagnostic.Create(
                                RemovedInterfaceNotRetiredRule,
                                location,
                                properties,
                                declaredInterface.Name));
                        }
                    }

                    foreach (var declaredClass in _data.Classes)
                    {
                        if (declaredClass.IsRetired || _visitedClasses.ContainsKey(GetDeclarationKey(declaredClass)))
                        {
                            continue;
                        }

                        var location = declaredClass.GetLocation(sourceText, _grainInterfacesFile.Path);
                        var properties = ImmutableDictionary<string, string?>.Empty
                            .Add(ClassNamePropertyKey, declaredClass.Name)
                            .Add(ActualAliasPropertyKey, declaredClass.Alias);

                        context.ReportDiagnostic(Diagnostic.Create(
                            RemovedGrainClassNotRetiredRule,
                            location,
                            properties,
                            declaredClass.Name));
                    }
                }
            }
        }

        private void AnalyzeGrainClass(SymbolAnalysisContext context, INamedTypeSymbol namedType)
        {
            var className = namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");

            if (_data is null)
            {
                _visitedClasses.TryAdd(className, true);
                RecordContractLocation(namedType);
                return;
            }

            var codeAlias = GetGrainType(namedType);
            var declaredClass = FindDeclaredClass(className, codeAlias);
            if (declaredClass is null || declaredClass.IsRetired)
            {
                _visitedClasses.TryAdd(className, true);
                var properties = ImmutableDictionary<string, string?>.Empty.Add(ClassNamePropertyKey, className);
                foreach (var location in namedType.Locations.Where(location => location.IsInSource))
                {
                    context.ReportDiagnostic(Diagnostic.Create(GrainClassNotDeclaredRule, location, properties, className));
                }

                return;
            }

            _visitedClasses.TryAdd(GetDeclarationKey(declaredClass), true);
            if (declaredClass.Alias is null
                || string.Equals(codeAlias, declaredClass.Alias, StringComparison.Ordinal))
            {
                return;
            }

            var aliasProperties = ImmutableDictionary<string, string?>.Empty
                .Add(ClassNamePropertyKey, className)
                .Add(ActualAliasPropertyKey, codeAlias);
            foreach (var location in namedType.Locations.Where(location => location.IsInSource))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    GrainClassAliasMismatchRule,
                    location,
                    aliasProperties,
                    className,
                    declaredClass.Alias ?? "<none>",
                    codeAlias ?? "<none>"));
            }
        }

        private bool IsRpcContract(INamedTypeSymbol type)
        {
            if (_iAddressableType is null)
            {
                return false;
            }

            return !SymbolEqualityComparer.Default.Equals(type, _iAddressableType)
                && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _iAddressableType));
        }

        private void RecordContractLocation(INamedTypeSymbol type)
        {
            var location = type.Locations.FirstOrDefault(candidate => candidate.IsInSource);
            if (location is not null)
            {
                Interlocked.CompareExchange(ref _firstContractLocation, location, null);
            }
        }

        private DeclaredGrainInterface? FindDeclaredInterface(
            string interfaceName,
            string? grainInterfaceType,
            bool allowLegacyNameMatch)
        {
            if (grainInterfaceType is not null)
            {
                var stableMatch = _data!.Interfaces.FirstOrDefault(candidate =>
                    string.Equals(GetDeclarationKey(candidate), grainInterfaceType, StringComparison.Ordinal));
                if (stableMatch is not null)
                {
                    return stableMatch;
                }

                if (allowLegacyNameMatch)
                {
                    return _data.Interfaces.FirstOrDefault(candidate =>
                        candidate.GrainInterfaceType is null
                        && string.Equals(candidate.Name, interfaceName, StringComparison.Ordinal));
                }

                return null;
            }

            return _data!.Interfaces.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, interfaceName, StringComparison.Ordinal));
        }

        private DeclaredGrainClass? FindDeclaredClass(string className, string? alias)
        {
            if (alias is not null)
            {
                var stableMatch = _data!.Classes.FirstOrDefault(candidate =>
                    string.Equals(GetDeclarationKey(candidate), alias, StringComparison.Ordinal));
                if (stableMatch is not null)
                {
                    return stableMatch;
                }

                return _data.Classes.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, className, StringComparison.Ordinal));
            }

            return _data!.Classes.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, className, StringComparison.Ordinal));
        }

        private static string GetDeclarationKey(DeclaredGrainInterface declaration)
            => declaration.GrainInterfaceType ?? GetDefaultGrainInterfaceType(declaration.Name);

        private static string GetDeclarationKey(DeclaredGrainClass declaration)
            => declaration.Alias ?? GetDefaultGrainType(declaration.Name);

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

    }

    internal static string GetGrainType(INamedTypeSymbol type)
    {
        if (GetStringAttributeValue(type, Constants.GrainTypeAttributeFullyQualifiedName) is { } grainType)
        {
            return grainType;
        }

        return GetDefaultGrainType(type);
    }

    internal static string GetDefaultGrainType(INamedTypeSymbol type)
    {
        var name = type.Name.ToLowerInvariant();

        const string GrainSuffix = "grain";
        if (name.EndsWith(GrainSuffix, StringComparison.Ordinal) && name.Length > GrainSuffix.Length)
        {
            name = name.Substring(0, name.Length - GrainSuffix.Length);
        }

        var arity = 0;
        for (var current = type; current is not null; current = current.ContainingType)
        {
            arity += current.Arity;
        }

        return arity > 0 ? $"{name}`{arity}" : name;
    }

    internal static string GetDefaultGrainType(string typeName)
    {
        var simpleNameStart = typeName.LastIndexOf('.') + 1;
        var simpleName = typeName.Substring(simpleNameStart);
        var genericStart = simpleName.IndexOf('<');
        if (genericStart >= 0)
        {
            simpleName = simpleName.Substring(0, genericStart);
        }

        var name = simpleName.ToLowerInvariant();
        const string GrainSuffix = "grain";
        if (name.EndsWith(GrainSuffix, StringComparison.Ordinal) && name.Length > GrainSuffix.Length)
        {
            name = name.Substring(0, name.Length - GrainSuffix.Length);
        }

        var arity = 0;
        var searchIndex = 0;
        while ((genericStart = typeName.IndexOf('<', searchIndex)) >= 0)
        {
            var genericEnd = typeName.IndexOf('>', genericStart + 1);
            if (genericEnd < 0)
            {
                break;
            }

            arity++;
            for (var index = genericStart + 1; index < genericEnd; index++)
            {
                if (typeName[index] == ',')
                {
                    arity++;
                }
            }

            searchIndex = genericEnd + 1;
        }

        return arity > 0 ? $"{name}`{arity}" : name;
    }

    internal static string GetGrainInterfaceType(INamedTypeSymbol type)
    {
        if (GetStringAttributeValue(type, Constants.GrainInterfaceTypeAttributeFullyQualifiedName) is { } grainInterfaceType)
        {
            return grainInterfaceType;
        }

        return GetDefaultGrainInterfaceType(type);
    }

    internal static string GetDefaultGrainInterfaceType(INamedTypeSymbol type)
    {
        if (type.ContainingType is { } containingType)
        {
            return $"{GetDefaultGrainInterfaceType(containingType)}+{type.MetadataName}";
        }

        return type.ContainingNamespace.IsGlobalNamespace
            ? type.MetadataName
            : $"{type.ContainingNamespace.ToDisplayString()}.{type.MetadataName}";
    }

    internal static string GetDefaultGrainInterfaceType(string typeName)
    {
        var segments = typeName.Split('.');
        var firstGenericSegment = Array.FindIndex(segments, segment => segment.IndexOf('<') >= 0);
        if (firstGenericSegment < 0)
        {
            return typeName;
        }

        for (var index = firstGenericSegment; index < segments.Length; index++)
        {
            var genericStart = segments[index].IndexOf('<');
            if (genericStart < 0)
            {
                continue;
            }

            var genericEnd = segments[index].LastIndexOf('>');
            var arity = 1;
            for (var characterIndex = genericStart + 1; characterIndex < genericEnd; characterIndex++)
            {
                if (segments[index][characterIndex] == ',')
                {
                    arity++;
                }
            }

            segments[index] = $"{segments[index].Substring(0, genericStart)}`{arity}";
        }

        return string.Join(".", segments.Take(firstGenericSegment))
            + (firstGenericSegment > 0 ? "." : string.Empty)
            + string.Join("+", segments.Skip(firstGenericSegment));
    }

    internal static string GetMethodSignature(IMethodSymbol method)
    {
        var sb = new StringBuilder();
        var methodId = GetAttributeValue(method, Constants.IdAttributeFullyQualifiedName);
        var methodAlias = GetStringAttributeValue(method, Constants.AliasAttributeFullyQualifiedName);
        sb.Append(methodId ?? methodAlias ?? method.Name);
        if (method.Arity > 0)
        {
            sb.Append('`');
            sb.Append(method.Arity);
        }
        sb.Append('(');

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(GetContractTypeName(method.Parameters[i].Type));
        }

        sb.Append(')');
        sb.Append(" -> ");
        sb.Append(GetContractTypeName(method.ReturnType));

        return sb.ToString();
    }

    internal static string GetClrMethodSignature(IMethodSymbol method)
    {
        var sb = new StringBuilder();
        sb.Append(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""));
        sb.Append('.');
        sb.Append(method.Name);
        if (method.Arity > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", method.TypeParameters.Select(parameter => parameter.Name)));
            sb.Append('>');
        }
        sb.Append('(');

        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(method.Parameters[i].Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            sb.Append(' ');
            sb.Append(method.Parameters[i].Name);
        }

        sb.Append(") -> ");
        sb.Append(method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        return sb.ToString();
    }

    internal static bool RequiresClrComment(IMethodSymbol method)
    {
        var methodId = GetAttributeValue(method, Constants.IdAttributeFullyQualifiedName)?.ToString();
        var methodAlias = GetStringAttributeValue(method, Constants.AliasAttributeFullyQualifiedName);
        if (methodId is not null && !string.Equals(methodId, method.Name, StringComparison.Ordinal)
            || methodAlias is not null && !string.Equals(methodAlias, method.Name, StringComparison.Ordinal))
        {
            return true;
        }

        return method.Parameters.Any(parameter => HasMeaningfulTypeAlias(parameter.Type))
            || HasMeaningfulTypeAlias(method.ReturnType);
    }

    internal static bool IdentityDiffersFromClrName(string? identity, INamedTypeSymbol type)
    {
        if (identity is null)
        {
            return false;
        }

        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
        return !string.Equals(identity, type.Name, StringComparison.Ordinal)
            && !string.Equals(identity, type.MetadataName, StringComparison.Ordinal)
            && !string.Equals(identity, fullName, StringComparison.Ordinal);
    }

    internal static string NormalizeLegacyMethodSignature(string signature)
        => Regex.Replace(signature, @"\s+[A-Za-z_]\w*(?=\s*[,)\]])", "");

    internal static string NormalizeStoredMemberSignature(string signature, string interfaceName)
    {
        var result = signature;
        if (result.StartsWith($"{interfaceName}.", StringComparison.Ordinal))
        {
            result = result.Substring(interfaceName.Length + 1);
        }

        result = Regex.Replace(result, @"^grain-interface\(""[^""]+""\)\.", "");
        return Regex.Replace(result, @"alias\(""([^""]+)""\)", "$1");
    }

    internal static bool IsMatchingMember(
        string declaredInterfaceName,
        string storedSignature,
        string? storedAlias,
        IMethodSymbol member)
    {
        var memberSignature = GetMethodSignature(member);
        if (storedAlias is not null)
        {
            if (!string.Equals(
                storedAlias,
                GetStringAttributeValue(member, Constants.AliasAttributeFullyQualifiedName),
                StringComparison.Ordinal))
            {
                return false;
            }

            var storedIdentity = storedAlias + GrainInterfaceFileParser.GetMethodAritySuffix(storedSignature);
            var parameterListStart = memberSignature.IndexOf('(');
            if (parameterListStart < 0
                || !string.Equals(
                    storedIdentity,
                    memberSignature.Substring(0, parameterListStart),
                    StringComparison.Ordinal))
            {
                return false;
            }

            var canonicalStoredSignature = GrainInterfaceFileParser.GetCanonicalMemberSignature(
                storedSignature,
                storedAlias);
            if (string.Equals(
                NormalizeStoredMemberSignature(canonicalStoredSignature, declaredInterfaceName),
                memberSignature,
                StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(
                GetNormalizedSignatureSuffix(storedSignature),
                GetNormalizedSignatureSuffix(GetClrMethodSignature(member)),
                StringComparison.Ordinal);
        }

        if (string.Equals(
            NormalizeStoredMemberSignature(storedSignature, declaredInterfaceName),
            memberSignature,
            StringComparison.Ordinal))
        {
            return true;
        }

        var normalized = NormalizeLegacyMethodSignature(storedSignature);
        var clrSignature = GetClrMethodSignature(member);
        var containingTypePrefix =
            $"{member.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "")}.";
        return string.Equals(normalized, NormalizeLegacyMethodSignature(clrSignature), StringComparison.Ordinal)
            || string.Equals(
                normalized,
                NormalizeLegacyMethodSignature(clrSignature.Substring(containingTypePrefix.Length)),
                StringComparison.Ordinal);
    }

    private static string GetNormalizedSignatureSuffix(string signature)
    {
        var parameterListStart = signature.IndexOf('(');
        return parameterListStart < 0
            ? NormalizeLegacyMethodSignature(signature)
            : NormalizeLegacyMethodSignature(signature.Substring(parameterListStart));
    }

    private static string GetContractTypeName(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return $"{GetContractTypeName(array.ElementType)}[{new string(',', array.Rank - 1)}]";
        }

        if (type is ITypeParameterSymbol typeParameter)
        {
            return typeParameter.Name;
        }

        if (type is not INamedTypeSymbol namedType)
        {
            return type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        }

        if (namedType.IsTupleType)
        {
            return $"({string.Join(", ", namedType.TupleElements.Select(element => GetContractTypeName(element.Type)))})";
        }

        var alias = GetStringAttributeValue(namedType.OriginalDefinition, Constants.AliasAttributeFullyQualifiedName);
        if (alias is not null)
        {
            return namedType.TypeArguments.IsEmpty
                ? alias
                : $"{alias}<{string.Join(", ", namedType.TypeArguments.Select(GetContractTypeName))}>";
        }

        if (namedType is
        {
            Name: "Task" or "ValueTask",
            ContainingNamespace: { } containingNamespace
        }
            && containingNamespace.ToDisplayString() == "System.Threading.Tasks")
        {
            if (namedType.TypeArguments.IsEmpty)
            {
                return namedType.Name;
            }

            return $"{namedType.Name}<{string.Join(", ", namedType.TypeArguments.Select(GetContractTypeName))}>";
        }

        if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return $"{GetContractTypeName(namedType.TypeArguments[0])}?";
        }

        if (namedType.TypeArguments.IsEmpty)
        {
            return namedType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        }

        var genericName = namedType.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var typeArgumentStart = genericName.IndexOf('<');
        if (typeArgumentStart >= 0)
        {
            genericName = genericName.Substring(0, typeArgumentStart);
        }

        return $"{genericName}<{string.Join(", ", namedType.TypeArguments.Select(GetContractTypeName))}>";
    }

    private static bool HasMeaningfulTypeAlias(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return HasMeaningfulTypeAlias(array.ElementType);
        }

        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        var alias = GetStringAttributeValue(namedType.OriginalDefinition, Constants.AliasAttributeFullyQualifiedName);
        if (IdentityDiffersFromClrName(alias, namedType.OriginalDefinition))
        {
            return true;
        }

        return namedType.TypeArguments.Any(HasMeaningfulTypeAlias);
    }

    private static object? GetAttributeValue(ISymbol symbol, string attributeName)
        => symbol.GetAttributes()
            .FirstOrDefault(attribute => string.Equals(attribute.AttributeClass?.ToDisplayString(), attributeName, StringComparison.Ordinal))
            ?.ConstructorArguments.FirstOrDefault().Value;

    private static string? GetStringAttributeValue(ISymbol symbol, string attributeName)
        => GetAttributeValue(symbol, attributeName) as string;
}

/// <summary>
/// Represents the parsed data from an OrleansContracts.txt file.
/// </summary>
internal sealed class GrainInterfaceData
{
    public List<DeclaredGrainInterface> Interfaces { get; } = new();

    public List<DeclaredGrainClass> Classes { get; } = new();
}

/// <summary>
/// Represents a declared grain interface in the OrleansContracts.txt file.
/// </summary>
internal sealed class DeclaredGrainInterface
{
    public DeclaredGrainInterface(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public string? Alias { get; set; }

    public string? GrainInterfaceType { get; set; }
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
/// Represents a declared grain interface member in the OrleansContracts.txt file.
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

    public Location GetLocation(SourceText sourceText, string filePath)
    {
        var lineSpan = sourceText.Lines.GetLinePositionSpan(Span);
        return Location.Create(filePath, Span, lineSpan);
    }
}

/// <summary>
/// Represents a declared grain class in the OrleansContracts.txt file.
/// </summary>
internal sealed class DeclaredGrainClass
{
    public DeclaredGrainClass(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public string? Alias { get; set; }

    public bool IsRetired { get; set; }

    public TextSpan Span { get; set; }

    public Location GetLocation(SourceText sourceText, string filePath)
    {
        var lineSpan = sourceText.Lines.GetLinePositionSpan(Span);
        return Location.Create(filePath, Span, lineSpan);
    }
}

/// <summary>
/// Parses OrleansContracts.txt files.
/// </summary>
internal static class GrainInterfaceFileParser
{
    // Regex patterns for parsing
    // Interface line: interface [GrainInterfaceType("x")] Namespace.IInterface<T> [Version(N)]
    // Or with retired: *RETIRED* interface [GrainInterfaceType("x")] Namespace.IInterface<T> [Version(N)]
    // The name can include generic type parameters like IMyGrain<T> or IMyGrain<TKey, TValue>
    private static readonly Regex InterfacePattern = new(
        @"^(?<retired>\*RETIRED\*\s*)?(?:interface\s+)?(\[GrainInterfaceType\(""(?<grainInterfaceType>[^""]+)""\)\]\s*)?(\[Alias\(""(?<alias>[^""]+)""\)\]\s*)?(?<name>[\w]+(?:<[\w,\s]+>)?(?:\.[\w]+(?:<[\w,\s]+>)?)*)\s*\[Version\((?<version>\d+)\)\]$",
        RegexOptions.Compiled);

    // Grain class line: class [GrainType("x")] Namespace.GrainClass
    // Or with retired: *RETIRED* class [GrainType("x")] Namespace.GrainClass
    private static readonly Regex GrainClassPattern = new(
        @"^(?<retired>\*RETIRED\*\s*)?class\s+(\[(?:GrainType|Alias)\(""(?<alias>[^""]+)""\)\]\s*)?(?<name>[\w]+(?:<[\w,\s]+>)?(?:\.[\w]+(?:<[\w,\s]+>)?)*)$",
        RegexOptions.Compiled);

    // Member line: [Alias("x")] Namespace.IInterface<T>.Method(params) -> ReturnType
    // The signature includes the full interface name (possibly generic) and method
    private static readonly Regex MemberPattern = new(
        @"^(\[Alias\(""(?<alias>[^""]+)""\)\]\s*)?(?<signature>.+\(.*\)\s*->\s*.+)$",
        RegexOptions.Compiled);

    internal static bool TryGetInterfaceName(string line, out string name)
    {
        var match = InterfacePattern.Match(StripClrComment(line));
        if (match.Success)
        {
            name = match.Groups["name"].Value;
            return true;
        }

        name = string.Empty;
        return false;
    }

    internal static bool TryGetGrainInterfaceType(string line, out string grainInterfaceType)
    {
        var match = InterfacePattern.Match(StripClrComment(line));
        if (match.Success && match.Groups["grainInterfaceType"].Success)
        {
            grainInterfaceType = match.Groups["grainInterfaceType"].Value;
            return true;
        }

        grainInterfaceType = string.Empty;
        return false;
    }

    internal static bool TryGetGrainClassName(string line, out string name)
    {
        var match = GrainClassPattern.Match(StripClrComment(line));
        if (match.Success)
        {
            name = match.Groups["name"].Value;
            return true;
        }

        name = string.Empty;
        return false;
    }

    internal static bool TryGetGrainClassType(string line, out string grainType)
    {
        var match = GrainClassPattern.Match(StripClrComment(line));
        if (match.Success && match.Groups["alias"].Success)
        {
            grainType = match.Groups["alias"].Value;
            return true;
        }

        grainType = string.Empty;
        return false;
    }

    internal static bool TryGetContractName(string line, out string name)
        => TryGetGrainClassName(line, out name) || TryGetInterfaceName(line, out name);

    internal static bool TryGetMemberSignature(string line, out string signature)
    {
        if (TryGetMemberDeclaration(line, out signature, out var alias))
        {
            signature = GetCanonicalMemberSignature(signature, alias);
            return true;
        }

        signature = string.Empty;
        return false;
    }

    internal static bool TryGetMemberDeclaration(string line, out string signature, out string? alias)
    {
        var match = MemberPattern.Match(StripClrComment(line));
        if (match.Success)
        {
            signature = match.Groups["signature"].Value;
            alias = match.Groups["alias"].Success ? match.Groups["alias"].Value : null;
            return true;
        }

        signature = string.Empty;
        alias = null;
        return false;
    }

    internal static string GetCanonicalMemberSignature(string signature, string? alias)
    {
        if (alias is null)
        {
            return signature;
        }

        var parameterListStart = signature.IndexOf('(');
        return parameterListStart < 0
            ? signature
            : alias + GetMethodAritySuffix(signature) + signature.Substring(parameterListStart);
    }

    internal static string GetMethodAritySuffix(string signature)
    {
        var parameterListStart = signature.IndexOf('(');
        if (parameterListStart < 0)
        {
            return string.Empty;
        }

        var methodStart = signature.LastIndexOf('.', parameterListStart - 1) + 1;
        var methodName = signature.Substring(methodStart, parameterListStart - methodStart);
        var arityMarker = methodName.LastIndexOf('`');
        if (arityMarker >= 0)
        {
            return methodName.Substring(arityMarker);
        }

        var genericStart = methodName.IndexOf('<');
        var genericEnd = methodName.LastIndexOf('>');
        if (genericStart < 0 || genericEnd <= genericStart)
        {
            return string.Empty;
        }

        var arity = 1;
        for (var index = genericStart + 1; index < genericEnd; index++)
        {
            if (methodName[index] == ',')
            {
                arity++;
            }
        }

        return $"`{arity}";
    }

    internal static string GetClrComment(string line)
    {
        const string Prefix = " # CLR: ";
        var index = line.IndexOf(Prefix, StringComparison.Ordinal);
        return index < 0 ? string.Empty : line.Substring(index);
    }

    internal static string StripClrComment(string line)
    {
        const string Prefix = " # CLR: ";
        var index = line.IndexOf(Prefix, StringComparison.Ordinal);
        return (index < 0 ? line : line.Substring(0, index)).Trim();
    }

    public static (GrainInterfaceData Data, List<Diagnostic>? Errors) Parse(SourceText sourceText, string filePath)
    {
        var data = new GrainInterfaceData();
        List<Diagnostic>? errors = null;
        DeclaredGrainInterface? currentInterface = null;

        foreach (var textLine in sourceText.Lines)
        {
            var lineText = StripClrComment(textLine.ToString());

            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(lineText) || lineText.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            // Try to match interface declaration
            var grainClassMatch = GrainClassPattern.Match(lineText);
            if (grainClassMatch.Success)
            {
                currentInterface = null;
                var name = grainClassMatch.Groups["name"].Value;
                var alias = grainClassMatch.Groups["alias"].Success ? grainClassMatch.Groups["alias"].Value : null;
                var identity = alias ?? GrainInterfaceVersionAnalyzer.GetDefaultGrainType(name);
                if (data.Classes.Any(candidate =>
                    string.Equals(
                        candidate.Alias ?? GrainInterfaceVersionAnalyzer.GetDefaultGrainType(candidate.Name),
                        identity,
                        StringComparison.Ordinal)))
                {
                    errors ??= new List<Diagnostic>();
                    var location = Location.Create(
                        filePath,
                        textLine.Span,
                        sourceText.Lines.GetLinePositionSpan(textLine.Span));
                    errors.Add(Diagnostic.Create(GrainInterfaceVersionAnalyzer.DuplicateGrainClassDeclarationRule, location, name));
                    continue;
                }

                data.Classes.Add(new DeclaredGrainClass(name)
                {
                    Alias = alias,
                    IsRetired = grainClassMatch.Groups["retired"].Success,
                    Span = textLine.Span
                });
                continue;
            }

            // Try to match interface declaration
            var interfaceMatch = InterfacePattern.Match(lineText);
            if (interfaceMatch.Success)
            {
                var name = interfaceMatch.Groups["name"].Value;
                var alias = interfaceMatch.Groups["alias"].Success ? interfaceMatch.Groups["alias"].Value : null;
                var grainInterfaceType = interfaceMatch.Groups["grainInterfaceType"].Success ? interfaceMatch.Groups["grainInterfaceType"].Value : null;
                if (!ushort.TryParse(interfaceMatch.Groups["version"].Value, out var version))
                {
                    currentInterface = null;
                    continue;
                }
                var isRetired = interfaceMatch.Groups["retired"].Success;

                var identity = grainInterfaceType ?? GrainInterfaceVersionAnalyzer.GetDefaultGrainInterfaceType(name);
                if (data.Interfaces.Any(candidate =>
                    string.Equals(
                        candidate.GrainInterfaceType
                            ?? GrainInterfaceVersionAnalyzer.GetDefaultGrainInterfaceType(candidate.Name),
                        identity,
                        StringComparison.Ordinal)))
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
                    GrainInterfaceType = grainInterfaceType,
                    Version = version,
                    IsRetired = isRetired,
                    Span = textLine.Span
                };
                data.Interfaces.Add(currentInterface);
                continue;
            }

            // Try to match member declaration
            var memberMatch = MemberPattern.Match(lineText);
            if (memberMatch.Success && currentInterface is not null)
            {
                var signature = memberMatch.Groups["signature"].Value;
                var alias = memberMatch.Groups["alias"].Success ? memberMatch.Groups["alias"].Value : null;

                currentInterface.Members[signature] = new DeclaredGrainMember(signature)
                {
                    Alias = alias,
                    Span = textLine.Span
                };
            }
        }

        return (data, errors);
    }
}
