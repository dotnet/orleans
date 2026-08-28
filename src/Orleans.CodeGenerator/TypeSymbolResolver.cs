using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal sealed class TypeSymbolResolver(Compilation compilation)
{
    private static readonly Dictionary<string, (string MetadataName, SpecialType SpecialType)> PrimitiveTypesByAlias =
        new(StringComparer.Ordinal)
        {
            ["bool"] = ("System.Boolean", SpecialType.System_Boolean),
            ["byte"] = ("System.Byte", SpecialType.System_Byte),
            ["sbyte"] = ("System.SByte", SpecialType.System_SByte),
            ["short"] = ("System.Int16", SpecialType.System_Int16),
            ["ushort"] = ("System.UInt16", SpecialType.System_UInt16),
            ["int"] = ("System.Int32", SpecialType.System_Int32),
            ["uint"] = ("System.UInt32", SpecialType.System_UInt32),
            ["long"] = ("System.Int64", SpecialType.System_Int64),
            ["ulong"] = ("System.UInt64", SpecialType.System_UInt64),
            ["float"] = ("System.Single", SpecialType.System_Single),
            ["double"] = ("System.Double", SpecialType.System_Double),
            ["decimal"] = ("System.Decimal", SpecialType.System_Decimal),
            ["char"] = ("System.Char", SpecialType.System_Char),
            ["string"] = ("System.String", SpecialType.System_String),
            ["object"] = ("System.Object", SpecialType.System_Object),
        };

    private static readonly Dictionary<string, SpecialType> SpecialTypesByMetadataName =
        PrimitiveTypesByAlias.Values.ToDictionary(
            static value => value.MetadataName,
            static value => value.SpecialType,
            StringComparer.Ordinal);

    private readonly Compilation _compilation = compilation;
    private FallbackIndex? _fallbackIndex;

    public bool TryResolveSerializableType(
        SerializableTypeModel model,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out INamedTypeSymbol? symbol)
    {
        if (model is null)
        {
            symbol = null;
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (TryResolveMetadataIdentity(
            model.MetadataIdentity,
            cancellationToken,
            out symbol,
            out var assemblyNameIsAmbiguous))
        {
            return true;
        }

        if (assemblyNameIsAmbiguous)
        {
            return false;
        }

        if (TryResolveTypeSyntax(model.TypeSyntax.SyntaxString, cancellationToken, out symbol))
        {
            return true;
        }

        foreach (var candidate in GetFallbackIndex(cancellationToken).AllTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(candidate.Name, model.Name, StringComparison.Ordinal)
                && string.Equals(candidate.GetNamespaceAndNesting(), model.Namespace, StringComparison.Ordinal)
                && candidate.GetAllTypeParameters().Count() == model.TypeParameters.Length)
            {
                symbol = candidate;
                return true;
            }
        }

        symbol = null;
        return false;
    }

    public bool TryResolveProxyInterface(
        ProxyInterfaceModel model,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out INamedTypeSymbol? symbol)
    {
        if (model is null)
        {
            symbol = null;
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (TryResolveMetadataIdentity(
            model.MetadataIdentity,
            cancellationToken,
            out symbol,
            out var assemblyNameIsAmbiguous))
        {
            if (symbol.TypeKind == TypeKind.Interface)
            {
                return true;
            }

            symbol = null;
            return false;
        }

        if (assemblyNameIsAmbiguous)
        {
            return false;
        }

        if (TryResolveTypeSyntax(model.InterfaceType.SyntaxString, cancellationToken, out symbol))
        {
            if (symbol.TypeKind == TypeKind.Interface)
            {
                return true;
            }

            symbol = null;
            return false;
        }

        foreach (var candidate in GetFallbackIndex(cancellationToken).AllTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.TypeKind == TypeKind.Interface
                && string.Equals(candidate.Name, model.Name, StringComparison.Ordinal)
                && string.Equals(candidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), model.InterfaceType.SyntaxString, StringComparison.Ordinal))
            {
                symbol = candidate;
                return true;
            }
        }

        symbol = null;
        return false;
    }

    public bool TryResolveType(
        TypeRef type,
        TypeMetadataIdentity metadataIdentity,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out INamedTypeSymbol? symbol)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryResolveMetadataIdentity(
            metadataIdentity,
            cancellationToken,
            out symbol,
            out var assemblyNameIsAmbiguous))
        {
            return true;
        }

        return !assemblyNameIsAmbiguous
            && TryResolveTypeSyntax(type.SyntaxString, cancellationToken, out symbol);
    }

    private bool TryResolveMetadataIdentity(
        TypeMetadataIdentity metadataIdentity,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out INamedTypeSymbol? symbol,
        out bool assemblyNameIsAmbiguous)
    {
        assemblyNameIsAmbiguous = false;
        if (metadataIdentity.IsEmpty)
        {
            symbol = null;
            return false;
        }

        if (!string.IsNullOrEmpty(metadataIdentity.AssemblyIdentity)
            || !string.IsNullOrEmpty(metadataIdentity.AssemblyName))
        {
            if (TryGetAssembly(
                metadataIdentity,
                cancellationToken,
                out var assembly,
                out assemblyNameIsAmbiguous))
            {
                symbol = assembly.GetTypeByMetadataName(metadataIdentity.MetadataName);
                return symbol is not null;
            }

            symbol = null;
            return false;
        }

        return TryResolveMetadataName(metadataIdentity.MetadataName, out symbol);
    }

    private bool TryGetAssembly(
        TypeMetadataIdentity metadataIdentity,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out IAssemblySymbol? assembly,
        out bool assemblyNameIsAmbiguous)
    {
        assemblyNameIsAmbiguous = false;
        if (!string.IsNullOrEmpty(metadataIdentity.AssemblyIdentity))
        {
            if (string.Equals(
                _compilation.Assembly.Identity.GetDisplayName(),
                metadataIdentity.AssemblyIdentity,
                StringComparison.Ordinal))
            {
                assembly = _compilation.Assembly;
                return true;
            }

            foreach (var reference in _compilation.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol candidate
                    && string.Equals(
                        candidate.Identity.GetDisplayName(),
                        metadataIdentity.AssemblyIdentity,
                        StringComparison.Ordinal))
                {
                    assembly = candidate;
                    return true;
                }
            }

            assembly = null;
            return false;
        }

        IAssemblySymbol? assemblyByName = null;
        if (!string.IsNullOrEmpty(metadataIdentity.AssemblyName)
            && string.Equals(_compilation.Assembly.Identity.Name, metadataIdentity.AssemblyName, StringComparison.Ordinal))
        {
            assemblyByName = _compilation.Assembly;
        }

        foreach (var reference in _compilation.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol candidate)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(metadataIdentity.AssemblyName)
                && string.Equals(candidate.Identity.Name, metadataIdentity.AssemblyName, StringComparison.Ordinal))
            {
                if (assemblyByName is not null)
                {
                    assembly = null;
                    assemblyNameIsAmbiguous = true;
                    return false;
                }

                assemblyByName = candidate;
            }
        }

        if (assemblyByName is not null)
        {
            assembly = assemblyByName;
            return true;
        }

        assembly = null;
        return false;
    }

    private bool TryResolveTypeSyntax(
        string typeSyntax,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out INamedTypeSymbol? symbol)
    {
        if (string.IsNullOrWhiteSpace(typeSyntax))
        {
            symbol = null;
            return false;
        }

        if (TryGetMetadataName(typeSyntax, allowGenericSyntax: false, out var metadataName)
            && TryResolveMetadataName(metadataName, out symbol))
        {
            return true;
        }

        var fallbackIndex = GetFallbackIndex(cancellationToken);
        if (fallbackIndex.TypesByKey.TryGetValue(NormalizeTypeKey(typeSyntax), out symbol))
        {
            return true;
        }

        return TryGetMetadataName(typeSyntax, allowGenericSyntax: true, out metadataName)
            && TryResolveMetadataName(metadataName, out symbol);
    }

    private bool TryResolveMetadataName(string metadataName, [NotNullWhen(true)] out INamedTypeSymbol? symbol)
    {
        symbol = _compilation.GetTypeByMetadataName(metadataName);
        if (symbol is null && TryGetSpecialType(metadataName, out var specialType))
        {
            symbol = _compilation.GetSpecialType(specialType);
        }

        return symbol is not null;
    }

    private static bool TryGetMetadataName(string typeSyntax, bool allowGenericSyntax, [NotNullWhen(true)] out string? metadataName)
    {
        metadataName = typeSyntax.Trim();
        if (metadataName.StartsWith("global::", StringComparison.Ordinal))
        {
            metadataName = metadataName.Substring("global::".Length);
        }

        var genericStart = metadataName.IndexOf('<');
        if (genericStart >= 0)
        {
            if (!allowGenericSyntax)
            {
                metadataName = null;
                return false;
            }

            metadataName = metadataName.Substring(0, genericStart);
        }

        metadataName = metadataName.Trim();
        if (metadataName.StartsWith("global::", StringComparison.Ordinal))
        {
            metadataName = metadataName.Substring("global::".Length);
        }

        if (PrimitiveTypesByAlias.TryGetValue(metadataName, out var primitiveType))
        {
            metadataName = primitiveType.MetadataName;
        }

        return !string.IsNullOrWhiteSpace(metadataName);
    }

    private static bool TryGetSpecialType(string metadataName, out SpecialType specialType)
        => SpecialTypesByMetadataName.TryGetValue(metadataName, out specialType);

    private FallbackIndex GetFallbackIndex(CancellationToken cancellationToken)
    {
        if (_fallbackIndex is { } fallbackIndex)
        {
            return fallbackIndex;
        }

        fallbackIndex = BuildFallbackIndex(cancellationToken);
        _fallbackIndex = fallbackIndex;
        return fallbackIndex;
    }

    private FallbackIndex BuildFallbackIndex(CancellationToken cancellationToken)
    {
        var typesByKey = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        var allTypes = new List<INamedTypeSymbol>();
        AddAssembly(_compilation.Assembly);

        foreach (var reference in _compilation.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
            {
                AddAssembly(assembly);
            }
        }

        return new FallbackIndex(typesByKey, allTypes);

        void AddAssembly(IAssemblySymbol assembly)
        {
            foreach (var type in assembly.GetDeclaredTypes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddType(type);
            }
        }

        void AddType(INamedTypeSymbol type)
        {
            allTypes.Add(type);
            AddKey(type.ToOpenTypeSyntax().ToString(), type);
            AddKey(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), type);
            AddKey(type.ToDisplayString(), type);
        }

        void AddKey(string key, INamedTypeSymbol type)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var normalizedKey = NormalizeTypeKey(key);
            if (!typesByKey.TryGetValue(normalizedKey, out _))
            {
                typesByKey.Add(normalizedKey, type);
            }
        }
    }

    private sealed class FallbackIndex(Dictionary<string, INamedTypeSymbol> typesByKey, List<INamedTypeSymbol> allTypes)
    {
        public Dictionary<string, INamedTypeSymbol> TypesByKey { get; } = typesByKey;
        public List<INamedTypeSymbol> AllTypes { get; } = allTypes;
    }

    private static string NormalizeTypeKey(string value)
        => string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));
}
