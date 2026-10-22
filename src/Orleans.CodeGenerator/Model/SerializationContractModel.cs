using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator.Model;

internal readonly record struct ContractTargetModel(TypeRef Type, string RuntimeTypeName, bool IsAccessible);

internal readonly record struct SerializationContractModel(
    string RegistrationMethod,
    ContractTargetModel Target,
    ContractTargetModel? Surrogate = null,
    SerializationTypeModel? SurrogateDescription = null,
    SerializationTypeModel? TargetDescription = null);

internal readonly record struct SerializationTypeModel(
    ContractTargetModel? Type,
    int ParameterIndex,
    int ArrayRank,
    EquatableArray<SerializationTypeModel> Arguments);

internal static class SerializationContractModelExtractor
{
    public static EquatableArray<SerializationContractModel> Extract(
        INamedTypeSymbol implementation,
        RegisteredCodecKind kind,
        Compilation compilation)
    {
        var contracts = ImmutableArray.CreateBuilder<SerializationContractModel>();
        foreach (var contract in implementation.AllInterfaces)
        {
            var method = (kind, TypeMetadataIdentity.Create(contract).MetadataName) switch
            {
                (RegisteredCodecKind.Serializer, "Orleans.Serialization.Codecs.IFieldCodec`1") => "AddSerializer",
                (RegisteredCodecKind.Serializer, "Orleans.Serialization.Serializers.IBaseCodec`1") => "AddBaseCodec",
                (RegisteredCodecKind.Serializer, "Orleans.Serialization.Serializers.IValueSerializer`1") => "AddValueSerializer",
                (RegisteredCodecKind.Copier, "Orleans.Serialization.Cloning.IDeepCopier`1") => "AddCopier",
                (RegisteredCodecKind.Copier, "Orleans.Serialization.Cloning.IBaseCopier`1") => "AddBaseCopier",
                (RegisteredCodecKind.Activator, "Orleans.Serialization.Activators.IActivator`1") => "AddActivator",
                (RegisteredCodecKind.Converter, "Orleans.IConverter`2") => "AddConverter",
                _ => null
            };
            if (method is null)
            {
                continue;
            }

            contracts.Add(new SerializationContractModel(
                method,
                CreateTarget(contract.TypeArguments[0], compilation),
                contract.TypeArguments.Length == 2 ? CreateTarget(contract.TypeArguments[1], compilation) : null,
                contract.TypeArguments.Length == 2 && ContainsTypeParameter(contract.TypeArguments[1])
                    ? CreateDescription(contract.TypeArguments[1], implementation.GetAllTypeParameters().ToArray(), compilation)
                    : null,
                ContainsTypeParameter(contract.TypeArguments[0])
                    ? CreateDescription(contract.TypeArguments[0], implementation.GetAllTypeParameters().ToArray(), compilation)
                    : null));
        }

        static SerializationTypeModel CreateDescription(ITypeSymbol type, ITypeParameterSymbol[] parameters, Compilation compilation)
        {
            if (type is ITypeParameterSymbol parameter)
            {
                var index = System.Array.FindIndex(parameters, candidate => SymbolEqualityComparer.Default.Equals(candidate, parameter));
                if (index < 0)
                {
                    throw new InvalidOperationException($"Surrogate parameter '{parameter.Name}' is not declared by its converter implementation.");
                }

                return new(null, index, 0, default);
            }

            if (type is IArrayTypeSymbol array && ContainsTypeParameter(array))
            {
                return new(null, -1, array.Rank, ImmutableArray.Create(CreateDescription(array.ElementType, parameters, compilation)));
            }

            if (type is INamedTypeSymbol named && ContainsTypeParameter(named))
            {
                return new(CreateTarget(named.OriginalDefinition, compilation), -1, 0,
                    named.GetAllTypeArguments().Select(argument => CreateDescription(argument, parameters, compilation)).ToImmutableArray());
            }

            return new(CreateTarget(type, compilation), -1, 0, default);
        }

        return contracts.Distinct().OrderBy(static contract => contract.RegistrationMethod, StringComparer.Ordinal)
            .ThenBy(static contract => contract.Target.Type.SyntaxString, StringComparer.Ordinal).ToImmutableArray();
    }

    private static ContractTargetModel CreateTarget(ITypeSymbol target, Compilation compilation)
    {
        if (target is IDynamicTypeSymbol)
        {
            target = compilation.GetSpecialType(SpecialType.System_Object);
        }

        if (target is ITypeParameterSymbol || target is IArrayTypeSymbol && ContainsTypeParameter(target))
        {
            return new(TypeRef.Empty, string.Empty, false);
        }

        if (target is INamedTypeSymbol { IsTupleType: true, TupleUnderlyingType: { } tuple })
        {
            target = tuple;
        }

        if (target is INamedTypeSymbol named && named.GetAllTypeArguments().Any(ContainsTypeParameter))
        {
            target = named.OriginalDefinition;
        }

        var open = target is INamedTypeSymbol generic && generic.GetAllTypeArguments().Any(ContainsTypeParameter);
        var syntax = string.Concat(target.ToDisplayParts(SymbolDisplayFormat.FullyQualifiedFormat)
            .Select(static part => part.Kind == SymbolDisplayPartKind.Keyword && part.ToString() == "dynamic" ? "object" : part.ToString()));
        if (open)
        {
            syntax = target is INamedTypeSymbol { IsTupleType: true } tupleDefinition
                ? "global::System.ValueTuple<" + new string(',', tupleDefinition.Arity - 1) + ">"
                : target.ToOpenTypeSyntax().ToString();
        }
        return new ContractTargetModel(new TypeRef(syntax), FormatTypeName(target, compilation),
            compilation.IsSymbolAccessibleWithin(target, compilation.Assembly));
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.GetAllTypeArguments().Any(ContainsTypeParameter),
        _ => false
    };

    private static string FormatTypeName(ITypeSymbol type, Compilation compilation)
    {
        if (type is IDynamicTypeSymbol)
        {
            type = compilation.GetSpecialType(SpecialType.System_Object);
        }

        if (type is IArrayTypeSymbol array)
        {
            var element = FormatTypeName(array.ElementType, compilation);
            var separator = element.LastIndexOf(", ", StringComparison.Ordinal);
            var suffix = array.Rank == 1 ? "[]" : "[" + new string(',', array.Rank - 1) + "]";
            return element.Insert(separator, suffix);
        }

        if (type is not INamedTypeSymbol named)
        {
            return string.Empty;
        }

        var identity = TypeMetadataIdentity.Create(named);
        var arguments = named.GetAllTypeArguments().ToArray();
        var genericArguments = arguments.Length > 0 && !arguments.Any(ContainsTypeParameter)
            ? "[" + string.Join(",", arguments.Select(argument => "[" + FormatTypeName(argument, compilation) + "]")) + "]"
            : string.Empty;
        return identity.MetadataName + genericArguments + ", " + identity.AssemblyName;
    }
}
