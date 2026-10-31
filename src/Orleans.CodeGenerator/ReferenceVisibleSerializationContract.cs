using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

/// <summary>
/// Certifies that the producer's complete logical serialization contract survives reference emission.
/// Consumers reconstruct it from declarations rather than private generated implementation fields.
/// </summary>
internal static class ReferenceVisibleSerializationContract
{
    internal const string KeyPrefix = "Orleans.CodeGenerator.ReferenceVisibleContract:";
    internal const string Complete = "1:complete";
    internal const string Opaque = "1:opaque";

    internal static string? CreateAttribute(ISerializableTypeDescription description, LibraryTypes library)
    {
        if (description is not SerializableTypeDescription model
            || !SymbolEqualityComparer.Default.Equals(model.Type.ContainingAssembly, library.Compilation.Assembly))
        {
            return null;
        }

        var complete = TryReadDeclaration(model.Type, library, out var declared, out _)
            && GetFailure(description, model.Type, library) is null
            && MatchesDeclaration(description, declared);
        var key = KeyPrefix + TypeMetadataIdentity.Create(model.Type).MetadataName;
        // An explicit opaque certificate also prevents a conflicting source-supplied positive certificate.
        return $"[assembly: global::System.Reflection.AssemblyMetadataAttribute({CodeText.StringLiteral(key)}, {CodeText.StringLiteral(complete ? Complete : Opaque)})]";
    }

    internal static bool MatchesDeclaration(ISerializableTypeDescription producer, ISerializableTypeDescription declared)
    {
        var members = new Dictionary<string, (uint Id, bool Constructor)>(StringComparer.Ordinal);
        foreach (var member in declared.Members)
        {
            var name = GetLogicalMember(member).MetadataName;
            if (members.ContainsKey(name))
            {
                return false;
            }

            members.Add(name, (member.FieldId, member.IsPrimaryConstructorParameter));
        }

        return members.Count == producer.Members.Count
            && producer.Members.All(member => members.TryGetValue(GetLogicalMember(member).MetadataName, out var key)
                && key == (member.FieldId, member.IsPrimaryConstructorParameter));
    }

    private static ISymbol GetLogicalMember(IMemberDescription member)
        => member.Symbol is IFieldSymbol field && (field.IsImplicitlyDeclared || field.IsCompilerGenerated())
            ? PropertyUtility.GetMatchingProperty(field) ?? member.Symbol
            : member.Symbol;

    private static string? GetFailure(ISerializableTypeDescription description, INamedTypeSymbol type, LibraryTypes library)
    {
        if (type.IsFileLocal || type.IsAbstract || !SerializerFactoryGenerator.HasSupportedModelConstruction(description))
        {
            return "reference-visible contracts require an accessible concrete type with supported default construction";
        }

        var compilation = library.Compilation;
        var friends = type.ContainingAssembly.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.InternalsVisibleToAttribute");
        if (!Retained(type) || !compilation.IsSymbolAccessibleWithin(type, compilation.Assembly))
        {
            return $"type '{type}' is inaccessible to the generated context";
        }

        var selection = SerializableSourceOutputGenerator.GetGenerateFieldIdsOptionFromType(type, library);
        foreach (var member in description.Members)
        {
            var symbol = GetLogicalMember(member);
            var visibleId = GeneratedCodeUtilities.GetId(library, symbol);
            if (visibleId is { } id ? id != member.FieldId : selection != GenerateFieldIds.PublicProperties)
            {
                return $"member '{symbol}' requires a reference-visible Id or type-local member selection policy";
            }

            if (!compilation.IsSymbolAccessibleWithin(member.Type, compilation.Assembly))
            {
                return $"member type '{member.Type}' is inaccessible to the generated context";
            }

            if (!Retained(symbol) || !compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly)
                || symbol switch
                {
                    IFieldSymbol field => field.IsReadOnly,
                    IPropertySymbol property => property.GetMethod is not { } getter
                        || property.SetMethod is not { IsInitOnly: false } setter
                        || !Retained(getter) || !Retained(setter)
                        || !compilation.IsSymbolAccessibleWithin(getter, compilation.Assembly)
                        || !compilation.IsSymbolAccessibleWithin(setter, compilation.Assembly),
                    _ => true
                })
            {
                return $"serialized member '{symbol}' must be readable and writable from generated code";
            }

            if (!SymbolEqualityComparer.Default.Equals(symbol, member.Symbol)
                && member.Symbol.HasAttribute(library.ImmutableAttribute)
                && !symbol.HasAttribute(library.ImmutableAttribute))
            {
                return $"member '{symbol}' has a copy policy on hidden backing storage";
            }
        }

        return null;

        bool Retained(ISymbol symbol)
        {
            for (ISymbol? current = symbol; current is not null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility switch
                    {
                        Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal => false,
                        Accessibility.Internal or Accessibility.ProtectedAndInternal => !friends,
                        _ => true
                    })
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal static bool TryReadDeclaration(INamedTypeSymbol type, LibraryTypes library,
        [NotNullWhen(true)] out SerializableTypeDescription? description, out string? failure)
    {
        var members = new FieldIdAssignmentHelper(type, [],
            SerializableSourceOutputGenerator.GetGenerateFieldIdsOptionFromType(type, library), library);
        if (type.TypeKind != TypeKind.Enum && !members.IsValidForSerialization)
        {
            description = null;
            failure = "the type declaration does not expose a complete serialization member selection";
            return false;
        }

        var dataMembers = type.TypeKind == TypeKind.Enum ? [] : SerializableSourceOutputGenerator.GetDataMembers(members).ToList();
        if (dataMembers.Any(member => !library.Compilation.IsSymbolAccessibleWithin(member.Type, library.Compilation.Assembly)))
        {
            description = null;
            failure = "a serialized member type is inaccessible to the generated context";
            return false;
        }

        description = new SerializableTypeDescription(library.Compilation, type, false, dataMembers, library);
        failure = GetFailure(description, type, library);
        return failure is null;
    }

    /// <summary>Indexes certificates once per producer within one context compilation.</summary>
    internal sealed class Index
    {
        private readonly Dictionary<IAssemblySymbol, Dictionary<string, string?>> _assemblies = new(SymbolEqualityComparer.Default);

        internal bool TryDescribe(INamedTypeSymbol type, LibraryTypes library,
            [NotNullWhen(true)] out SerializableTypeDescription? description, out string? failure)
        {
            description = null;
            if (!_assemblies.TryGetValue(type.ContainingAssembly, out var certificates))
            {
                certificates = new(StringComparer.Ordinal);
                foreach (var attribute in type.ContainingAssembly.GetAttributes())
                {
                    if (attribute.AttributeClass?.ToDisplayString() != "System.Reflection.AssemblyMetadataAttribute"
                        || attribute.ConstructorArguments.Length != 2
                        || attribute.ConstructorArguments[0].Value is not string key
                        || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var name = key.Substring(KeyPrefix.Length);
                    certificates[name] = certificates.ContainsKey(name) ? null : attribute.ConstructorArguments[1].Value as string;
                }

                _assemblies.Add(type.ContainingAssembly, certificates);
            }

            var metadataName = TypeMetadataIdentity.Create(type).MetadataName;
            if (!certificates.TryGetValue(metadataName, out var certificate) || certificate == Opaque)
            {
                failure = "provide full implementation metadata or a producer-certified reference-visible serialization contract with accessible members";
                return false;
            }

            if (certificate != Complete)
            {
                failure = "the reference-visible serialization certificate is duplicated, malformed, or unsupported";
                return false;
            }

            return TryReadDeclaration(type, library, out description, out failure);
        }
    }
}
