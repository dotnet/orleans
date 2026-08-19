using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal sealed class TypeSymbolResolver(Compilation compilation)
{
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
        if (TryResolveMetadataIdentity(model.MetadataIdentity, cancellationToken, out symbol)
            || TryResolveTypeSyntax(model.TypeSyntax.SyntaxString, cancellationToken, out symbol))
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
        if (TryResolveMetadataIdentity(model.MetadataIdentity, cancellationToken, out symbol)
            || TryResolveTypeSyntax(model.InterfaceType.SyntaxString, cancellationToken, out symbol))
        {
            return symbol.TypeKind == TypeKind.Interface;
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

    private bool TryResolveMetadataIdentity(
        TypeMetadataIdentity metadataIdentity,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out INamedTypeSymbol? symbol)
    {
        if (metadataIdentity.IsEmpty)
        {
            symbol = null;
            return false;
        }

        if (!string.IsNullOrEmpty(metadataIdentity.AssemblyIdentity)
            || !string.IsNullOrEmpty(metadataIdentity.AssemblyName))
        {
            if (TryGetAssembly(metadataIdentity, cancellationToken, out var assembly))
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
        [NotNullWhen(true)] out IAssemblySymbol? assembly)
    {
        if (IsMatchingAssembly(_compilation.Assembly, metadataIdentity))
        {
            assembly = _compilation.Assembly;
            return true;
        }

        IAssemblySymbol? assemblyByName = null;
        foreach (var reference in _compilation.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol candidate)
            {
                continue;
            }

            if (IsMatchingAssembly(candidate, metadataIdentity))
            {
                assembly = candidate;
                return true;
            }

            if (string.IsNullOrEmpty(metadataIdentity.AssemblyIdentity)
                && !string.IsNullOrEmpty(metadataIdentity.AssemblyName)
                && string.Equals(candidate.Identity.Name, metadataIdentity.AssemblyName, StringComparison.Ordinal))
            {
                if (assemblyByName is not null)
                {
                    assembly = null;
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

    private static bool IsMatchingAssembly(IAssemblySymbol assembly, TypeMetadataIdentity metadataIdentity)
    {
        if (!string.IsNullOrEmpty(metadataIdentity.AssemblyIdentity))
        {
            return string.Equals(assembly.Identity.GetDisplayName(), metadataIdentity.AssemblyIdentity, StringComparison.Ordinal);
        }

        return !string.IsNullOrEmpty(metadataIdentity.AssemblyName)
            && string.Equals(assembly.Identity.Name, metadataIdentity.AssemblyName, StringComparison.Ordinal);
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

        metadataName = metadataName switch
        {
            "bool" => "System.Boolean",
            "byte" => "System.Byte",
            "sbyte" => "System.SByte",
            "short" => "System.Int16",
            "ushort" => "System.UInt16",
            "int" => "System.Int32",
            "uint" => "System.UInt32",
            "long" => "System.Int64",
            "ulong" => "System.UInt64",
            "float" => "System.Single",
            "double" => "System.Double",
            "decimal" => "System.Decimal",
            "char" => "System.Char",
            "string" => "System.String",
            "object" => "System.Object",
            _ => metadataName,
        };

        return !string.IsNullOrWhiteSpace(metadataName);
    }

    private static bool TryGetSpecialType(string metadataName, out SpecialType specialType)
    {
        specialType = metadataName switch
        {
            "System.Boolean" => SpecialType.System_Boolean,
            "System.Byte" => SpecialType.System_Byte,
            "System.SByte" => SpecialType.System_SByte,
            "System.Int16" => SpecialType.System_Int16,
            "System.UInt16" => SpecialType.System_UInt16,
            "System.Int32" => SpecialType.System_Int32,
            "System.UInt32" => SpecialType.System_UInt32,
            "System.Int64" => SpecialType.System_Int64,
            "System.UInt64" => SpecialType.System_UInt64,
            "System.Single" => SpecialType.System_Single,
            "System.Double" => SpecialType.System_Double,
            "System.Decimal" => SpecialType.System_Decimal,
            "System.Char" => SpecialType.System_Char,
            "System.String" => SpecialType.System_String,
            "System.Object" => SpecialType.System_Object,
            _ => SpecialType.None,
        };

        return specialType != SpecialType.None;
    }

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
