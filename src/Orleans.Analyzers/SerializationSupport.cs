using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Orleans.Analyzers;

/// <summary>
/// Discovers explicit serializer and surrogate registrations, including the built-in Orleans codecs.
/// Runtime predicates and dependency-injection factories are intentionally outside this static contract.
/// </summary>
internal sealed class SerializationSupport
{
    private readonly Compilation _compilation;
    private readonly Dictionary<INamedTypeSymbol, List<Registration>> _registrations = new(SymbolEqualityComparer.Default);

    private SerializationSupport(Compilation compilation) => _compilation = compilation;

    public static SerializationSupport Create(Compilation compilation, IAssemblySymbol annotations, CancellationToken cancellationToken)
    {
        var result = new SerializationSupport(compilation);
        var registerSerializer = compilation.GetTypeByMetadataName("Orleans.RegisterSerializerAttribute");
        var registerConverter = compilation.GetTypeByMetadataName("Orleans.RegisterConverterAttribute");
        var fieldCodec = compilation.GetTypeByMetadataName("Orleans.Serialization.Codecs.IFieldCodec`1");
        var baseCodec = compilation.GetTypeByMetadataName("Orleans.Serialization.Serializers.IBaseCodec`1");
        var valueSerializer = compilation.GetTypeByMetadataName("Orleans.Serialization.Serializers.IValueSerializer`1");
        var converter = compilation.GetTypeByMetadataName("Orleans.IConverter`2");
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols.Prepend(compilation.Assembly))
        {
            // Assemblies without a reference to the annotations cannot declare a registration.
            if (!SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly)
                && !assembly.Modules.Any(module => module.ReferencedAssemblySymbols.Contains(annotations, SymbolEqualityComparer.Default)))
            {
                continue;
            }

            var pending = new Stack<INamespaceOrTypeSymbol>();
            pending.Push(assembly.GlobalNamespace);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                foreach (var member in current.GetMembers().OfType<INamespaceOrTypeSymbol>()) pending.Push(member);
                if (current is not INamedTypeSymbol type || type.IsAbstract
                    || type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)) continue;
                var serializer = HasAttribute(type, registerSerializer);
                var surrogateConverter = HasAttribute(type, registerConverter);
                if (!serializer && !surrogateConverter) continue;
                foreach (var implemented in type.AllInterfaces)
                {
                    // CodecProvider wraps base codecs and value serializers in field codecs.
                    var isCodec = serializer && (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, fieldCodec)
                        || SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, baseCodec)
                        || SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, valueSerializer));
                    var isConverter = surrogateConverter && SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, converter);
                    if ((isCodec || isConverter) && implemented.TypeArguments[0] is INamedTypeSymbol target)
                    {
                        if (!result._registrations.TryGetValue(target.OriginalDefinition, out var registrations))
                        {
                            result._registrations.Add(target.OriginalDefinition, registrations = []);
                        }

                        registrations.Add(new(target, isConverter ? implemented.TypeArguments[1] : null,
                            assembly.Name == "Orleans.Serialization", type));
                    }
                }
            }
        }

        return result;
    }

    public bool TryGetSupport(INamedTypeSymbol type, out bool inspectArguments, out ITypeSymbol? surrogate)
    {
        if (_registrations.TryGetValue(type.OriginalDefinition, out var registrations))
        {
            foreach (var registration in registrations)
            {
                var arguments = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
                if (!Matches(registration.Target, type, arguments)) continue;
                // Do not infer support from a constrained open registration for an ineligible construction.
                if (GetTypeParameters(registration.Implementation).Any(parameter =>
                    !arguments.TryGetValue(parameter, out var argument) || !SatisfiesConstraints(parameter, argument))) continue;
                inspectArguments = registration.InspectArguments;
                surrogate = Substitute(registration.Surrogate, arguments);
                return true;
            }
        }

        inspectArguments = false;
        surrogate = null;
        return false;
    }

    private static bool HasAttribute(INamedTypeSymbol type, INamedTypeSymbol? attribute) => attribute is not null
        && type.GetAttributes().Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute));

    private static IEnumerable<ITypeParameterSymbol> GetTypeParameters(INamedTypeSymbol type)
    {
        for (var declaringType = type; declaringType is not null; declaringType = declaringType.ContainingType)
        {
            foreach (var parameter in declaringType.TypeParameters) yield return parameter;
        }
    }

    private static bool Matches(ITypeSymbol pattern, ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol> arguments)
    {
        if (pattern is ITypeParameterSymbol parameter)
        {
            if (arguments.TryGetValue(parameter, out var previous)) return SymbolEqualityComparer.Default.Equals(previous, type);
            arguments.Add(parameter, type);
            return true;
        }

        if (pattern is IArrayTypeSymbol leftArray && type is IArrayTypeSymbol rightArray)
        {
            return leftArray.Rank == rightArray.Rank && Matches(leftArray.ElementType, rightArray.ElementType, arguments);
        }

        if (pattern is not INamedTypeSymbol left || type is not INamedTypeSymbol right
            || !SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition))
        {
            return SymbolEqualityComparer.Default.Equals(pattern, type);
        }

        return left.TypeArguments.Zip(right.TypeArguments, (a, b) => Matches(a, b, arguments)).All(static match => match)
            && (left.ContainingType is null || right.ContainingType is not null && Matches(left.ContainingType, right.ContainingType, arguments));
    }

    private static bool SatisfiesConstraints(ITypeParameterSymbol parameter, ITypeSymbol argument) =>
        (!parameter.HasReferenceTypeConstraint || argument.IsReferenceType)
        && (!parameter.HasValueTypeConstraint || argument.IsValueType && argument.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
        && (!parameter.HasUnmanagedTypeConstraint || argument.IsUnmanagedType)
        // Arbitrary substituted base/interface constraints require runtime registration knowledge. Be conservative.
        && parameter.ConstraintTypes.Length == 0
        && (!parameter.HasConstructorConstraint || argument.IsValueType
            || argument is INamedTypeSymbol { IsAbstract: false } named && named.InstanceConstructors.Any(static constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0));

    private ITypeSymbol? Substitute(ITypeSymbol? type, Dictionary<ITypeParameterSymbol, ITypeSymbol> arguments)
    {
        if (type is ITypeParameterSymbol parameter && arguments.TryGetValue(parameter, out var argument)) return argument;
        if (type is IArrayTypeSymbol array)
        {
            return _compilation.CreateArrayTypeSymbol(Substitute(array.ElementType, arguments)!, array.Rank);
        }

        if (type is INamedTypeSymbol { IsGenericType: true } named)
        {
            var definition = named.ContainingType is { } containingType
                ? ((INamedTypeSymbol)Substitute(containingType, arguments)!).GetTypeMembers(named.Name, named.Arity).Single()
                : named.ConstructedFrom;
            return named.Arity == 0 ? definition
                : definition.Construct(named.TypeArguments.Select(argument => Substitute(argument, arguments)!).ToArray());
        }

        return type;
    }

    private sealed record Registration(INamedTypeSymbol Target, ITypeSymbol? Surrogate, bool InspectArguments, INamedTypeSymbol Implementation);
}
