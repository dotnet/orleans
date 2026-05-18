using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Hashing;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

internal static class GeneratedSourceOutput
{
    private const string GeneratedCodeWarningDisable = "#pragma warning disable CS1591, RS0016, RS0041";
    private const string GeneratedCodeWarningRestore = "#pragma warning restore CS1591, RS0016, RS0041";

    internal static void EmitSourceOutputResult(SourceProductionContext context, SourceOutputResult result)
    {
        if (result.Diagnostic is { } diagnostic)
        {
            context.ReportDiagnostic(diagnostic);
            return;
        }

        if (result.SourceEntry is { } sourceEntry)
        {
            context.AddSource(sourceEntry.HintName, sourceEntry.SourceText);
        }
    }

    internal static ImmutableArray<SerializableTypeResult> DeduplicateSerializableTypeResults(
        ImmutableArray<SerializableTypeResult> results)
    {
        if (results.IsDefaultOrEmpty)
        {
            return [];
        }

        var models = new Dictionary<string, SerializableTypeResult>(StringComparer.Ordinal);
        var diagnostics = new Dictionary<string, SerializableTypeResult>(StringComparer.Ordinal);
        foreach (var result in OrderSerializableTypeResultsForCanonicalSelection(results))
        {
            if (result.Model is not null)
            {
                var key = CreateSerializableTypeDedupeKey(result);
                if (!models.ContainsKey(key))
                {
                    models.Add(key, result);
                }
            }
            else if (result.Diagnostic is { } diagnostic)
            {
                var key = $"{CreateSerializableTypeDedupeKey(result)}|{diagnostic.Id}";
                if (!diagnostics.ContainsKey(key))
                {
                    diagnostics.Add(key, result);
                }
            }
        }

        return [.. OrderSerializableTypeResultsForEmission(models.Values.Concat(diagnostics.Values))];
    }

    internal static ImmutableArray<SerializableTypeModel> GetSerializableTypeModels(
        ImmutableArray<SerializableTypeResult> results)
    {
        if (results.IsDefaultOrEmpty)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<SerializableTypeModel>();
        foreach (var result in results)
        {
            if (result.Model is { } model)
            {
                builder.Add(model);
            }
        }

        return ModelExtractor.DeduplicateSerializableTypes(builder.ToImmutable());
    }

    internal static IOrderedEnumerable<SerializableTypeResult> OrderSerializableTypeResultsForCanonicalSelection(
        IEnumerable<SerializableTypeResult> results)
        => results
            .Where(static result => result.Model is not null || result.Diagnostic is not null)
            .OrderBy(static result => result.SourceLocation.SourceOrderGroup)
            .ThenBy(static result => result.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static result => result.SourceLocation.Position)
            .ThenBy(static result => result.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static result => result.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static result => result.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static result => result.TypeSyntax, StringComparer.Ordinal)
            .ThenBy(static result => result.Diagnostic?.Id ?? string.Empty, StringComparer.Ordinal);

    internal static IOrderedEnumerable<SerializableTypeResult> OrderSerializableTypeResultsForEmission(
        IEnumerable<SerializableTypeResult> results)
        => results
            .OrderBy(static result => result.Model is null ? 1 : 0)
            .ThenBy(static result => result.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static result => result.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static result => result.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static result => result.TypeSyntax, StringComparer.Ordinal)
            .ThenBy(static result => result.Diagnostic?.Id ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static result => result.SourceLocation.SourceOrderGroup)
            .ThenBy(static result => result.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static result => result.SourceLocation.Position);

    internal static string CreateSerializableTypeDedupeKey(SerializableTypeResult result)
        => CreateTypeDedupeKey(result.MetadataIdentity, result.TypeSyntax);

    internal static string CreateTypeDedupeKey(TypeMetadataIdentity metadataIdentity, string typeSyntax)
    {
        if (!metadataIdentity.IsEmpty)
        {
            return string.Join(
                "|",
                "M",
                metadataIdentity.AssemblyIdentity ?? string.Empty,
                metadataIdentity.AssemblyName ?? string.Empty,
                metadataIdentity.MetadataName ?? string.Empty);
        }

        return string.Join("|", "S", typeSyntax ?? string.Empty);
    }

    internal static ImmutableArray<SourceOutputResult> DeduplicateSourceOutputs(
        ImmutableArray<SourceOutputResult>.Builder sourceEntries)
        => DeduplicateSourceOutputs(sourceEntries.ToImmutable());

    internal static ImmutableArray<SourceOutputResult> DeduplicateSourceOutputs(
        ImmutableArray<SourceOutputResult> sourceEntries)
    {
        var emittedSourcesByOriginalHintName = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var emittedSourceByHintName = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<SourceOutputResult>();
        foreach (var sourceOutput in sourceEntries)
        {
            if (sourceOutput.SourceEntry is not { } entry)
            {
                result.Add(sourceOutput);
                continue;
            }

            var source = entry.Source ?? string.Empty;
            if (!emittedSourcesByOriginalHintName.TryGetValue(entry.HintName, out var emittedSources))
            {
                emittedSources = new Dictionary<string, string>(StringComparer.Ordinal);
                emittedSourcesByOriginalHintName.Add(entry.HintName, emittedSources);
            }

            if (emittedSources.ContainsKey(source))
            {
                continue;
            }

            if (!emittedSourceByHintName.TryGetValue(entry.HintName, out var emittedSource))
            {
                emittedSources.Add(source, entry.HintName);
                emittedSourceByHintName.Add(entry.HintName, source);
                result.Add(sourceOutput);
                continue;
            }

            if (string.Equals(emittedSource, source, StringComparison.Ordinal))
            {
                emittedSources.Add(source, entry.HintName);
                continue;
            }

            var uniqueHintName = CreateDistinctSourceHintName(entry.HintName, source, emittedSourceByHintName);
            emittedSources.Add(source, uniqueHintName);
            emittedSourceByHintName.Add(uniqueHintName, source);
            result.Add(SourceOutputResult.FromSource(new GeneratedSourceEntry(uniqueHintName, source)));
        }

        return NormalizeSourceOutputs(result.ToImmutable());
    }

    internal static ImmutableArray<SourceOutputResult> NormalizeSourceOutputs(ImmutableArray<SourceOutputResult> sourceOutputs)
        => StructuralEquality.Normalize(sourceOutputs);

    internal static GeneratedSourceEntry CreateSerializableSourceEntry(
        string assemblyName,
        string typeName,
        TypeMetadataIdentity metadataIdentity,
        string hintGeneratedNamespace,
        int genericArity,
        ClassDeclarationSyntax serializer,
        ClassDeclarationSyntax? copier,
        ClassDeclarationSyntax? activator,
        string generatedNamespace)
    {
        var namespacedMembers = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
        AddMember(namespacedMembers, generatedNamespace, serializer);
        if (copier is not null)
        {
            AddMember(namespacedMembers, generatedNamespace, copier);
        }

        if (activator is not null)
        {
            AddMember(namespacedMembers, generatedNamespace, activator);
        }

        return new GeneratedSourceEntry(
            CreateSerializableHintName(assemblyName, typeName, metadataIdentity, hintGeneratedNamespace, genericArity),
            CreateSourceString(CreateCompilationUnit(namespacedMembers)));
    }

    internal static void AddMember(
        Dictionary<string, List<MemberDeclarationSyntax>> namespacedMembers,
        string ns,
        MemberDeclarationSyntax member)
    {
        var namespaceName = ns ?? string.Empty;
        if (!namespacedMembers.TryGetValue(namespaceName, out var members))
        {
            members = [];
            namespacedMembers[namespaceName] = members;
        }

        members.Add(member);
    }

    internal static string CreateSourceString(CompilationUnitSyntax unit)
    {
        return $"{GeneratedCodeWarningDisable}\r\n{unit.NormalizeWhitespace().ToFullString()}\r\n{GeneratedCodeWarningRestore}";
    }

    internal static CompilationUnitSyntax CreateCompilationUnit(
        Dictionary<string, List<MemberDeclarationSyntax>> namespacedMembers,
        SyntaxList<AttributeListSyntax> attributeLists = default)
    {
        var unit = SyntaxFactory.CompilationUnit().WithAttributeLists(attributeLists);
        var usingDirectives = SyntaxFactory.List(
        [
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("global::Orleans.Serialization.Codecs")),
            SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("global::Orleans.Serialization.GeneratedCodeHelpers")),
        ]);
        var members = new List<MemberDeclarationSyntax>(namespacedMembers.Count);

        foreach (var pair in namespacedMembers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                members.AddRange(pair.Value);
                continue;
            }

            members.Add(
                SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(pair.Key))
                    .WithUsings(usingDirectives)
                    .WithMembers(SyntaxFactory.List(pair.Value)));
        }

        return unit.WithMembers(SyntaxFactory.List(members));
    }

    internal static string CreateSerializableHintName(
        string assemblyName,
        string typeName,
        TypeMetadataIdentity metadataIdentity,
        string generatedNamespace,
        int genericArity)
    {
        var hash = CreateHintNameHash(metadataIdentity, generatedNamespace, typeName, genericArity);

        return $"{assemblyName}.orleans.ser.{SanitizeHintComponent(typeName)}.{hash}.g.cs";
    }

    internal static string CreateProxyHintName(string assemblyName, ProxyInterfaceDescription interfaceDescription)
    {
        var interfaceName = interfaceDescription.InterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var hash = CreateHintNameHash(
            TypeMetadataIdentity.Create(interfaceDescription.InterfaceType),
            interfaceDescription.GeneratedNamespace,
            interfaceName,
            interfaceDescription.TypeParameters.Count);

        return $"{assemblyName}.orleans.proxy.{SanitizeHintComponent(interfaceName)}.{hash}.g.cs";
    }

    internal static string CreateMetadataHintName(string assemblyName)
        => $"{assemblyName}.orleans.metadata.g.cs";

    internal static string CreateHintNameHash(
        TypeMetadataIdentity metadataIdentity,
        string generatedNamespace,
        string syntaxString,
        int genericArity)
    {
        var builder = new StringBuilder();
        AppendHashComponent(builder, metadataIdentity.AssemblyIdentity);
        AppendHashComponent(builder, metadataIdentity.AssemblyName);
        AppendHashComponent(builder, metadataIdentity.MetadataName);
        AppendHashComponent(builder, generatedNamespace);
        AppendHashComponent(builder, syntaxString);
        AppendHashComponent(builder, genericArity.ToString(CultureInfo.InvariantCulture));

        return CreateStableHash(builder.ToString());
    }

    internal static string CreateStableHash(string value)
        => HexConverter.ToString(XxHash32.Hash(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    internal static void AppendHashComponent(StringBuilder builder, string value)
    {
        builder.Append(value?.Length ?? 0);
        builder.Append(':');
        builder.Append(value ?? string.Empty);
        builder.Append('|');
    }

    internal static string CreateDistinctSourceHintName(
        string hintName,
        string source,
        Dictionary<string, string> emittedSourceByHintName)
    {
        var sourceHash = CreateStableHash(source);
        var candidate = InsertHintNameComponent(hintName, $"collision.{sourceHash}");
        if (!emittedSourceByHintName.ContainsKey(candidate))
        {
            return candidate;
        }

        for (var index = 1; ; index++)
        {
            candidate = InsertHintNameComponent(hintName, $"collision.{sourceHash}.{index}");
            if (!emittedSourceByHintName.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    internal static string InsertHintNameComponent(string hintName, string component)
    {
        const string GeneratedSourceSuffix = ".g.cs";
        if (hintName.EndsWith(GeneratedSourceSuffix, StringComparison.Ordinal))
        {
            return $"{hintName.Substring(0, hintName.Length - GeneratedSourceSuffix.Length)}.{component}{GeneratedSourceSuffix}";
        }

        const string SourceSuffix = ".cs";
        if (hintName.EndsWith(SourceSuffix, StringComparison.Ordinal))
        {
            return $"{hintName.Substring(0, hintName.Length - SourceSuffix.Length)}.{component}{SourceSuffix}";
        }

        return $"{hintName}.{component}";
    }

    internal static string SanitizeHintComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "generated";
        }

        var builder = new StringBuilder(value.Length);
        var previousCharacterWasUnderscore = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '.')
            {
                builder.Append(character);
                previousCharacterWasUnderscore = false;
            }
            else if (!previousCharacterWasUnderscore)
            {
                builder.Append('_');
                previousCharacterWasUnderscore = true;
            }
        }

        var result = builder.ToString().Trim('_', '.');
        return result.Length > 0 ? result : "generated";
    }
}


