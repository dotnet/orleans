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
    private readonly List<Registration> _arrayRegistrations = [];

    private SerializationSupport(Compilation compilation) => _compilation = compilation;

    internal enum Requirement
    {
        Field,
        Base,
        Value
    }

    public static SerializationSupport Create(Compilation compilation, IAssemblySymbol annotations, CancellationToken cancellationToken)
    {
        var result = new SerializationSupport(compilation);
        var registerSerializer = compilation.GetTypeByMetadataName("Orleans.RegisterSerializerAttribute");
        var registerConverter = compilation.GetTypeByMetadataName("Orleans.RegisterConverterAttribute");
        var fieldCodec = compilation.GetTypeByMetadataName("Orleans.Serialization.Codecs.IFieldCodec`1");
        var baseCodec = compilation.GetTypeByMetadataName("Orleans.Serialization.Serializers.IBaseCodec`1");
        var valueSerializer = compilation.GetTypeByMetadataName("Orleans.Serialization.Serializers.IValueSerializer`1");
        var converter = compilation.GetTypeByMetadataName("Orleans.IConverter`2");
        var derivedTypeCodec = compilation.GetTypeByMetadataName("Orleans.Serialization.Codecs.IDerivedTypeCodec");
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
                var supportsDerivedTypes = derivedTypeCodec is not null && type.AllInterfaces.Any(candidate =>
                    SymbolEqualityComparer.Default.Equals(candidate, derivedTypeCodec));
                foreach (var implemented in type.AllInterfaces)
                {
                    var kind = serializer && SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, fieldCodec)
                        ? RegistrationKind.FieldCodec
                        : serializer && SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, baseCodec)
                            ? RegistrationKind.BaseCodec
                            : serializer && SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, valueSerializer)
                                ? RegistrationKind.ValueSerializer
                                : surrogateConverter && SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, converter)
                                    ? RegistrationKind.Converter
                                    : (RegistrationKind?)null;
                    if (kind is { } registrationKind && implemented.TypeArguments[0] is { } target
                        && target is INamedTypeSymbol or IArrayTypeSymbol)
                    {
                        var registration = new Registration(target,
                            registrationKind == RegistrationKind.Converter ? implemented.TypeArguments[1] : null,
                            assembly.Name == "Orleans.Serialization", type, registrationKind,
                            supportsDerivedTypes && registrationKind == RegistrationKind.FieldCodec);
                        if (target is IArrayTypeSymbol)
                        {
                            result._arrayRegistrations.Add(registration);
                        }
                        else
                        {
                            var namedTarget = (INamedTypeSymbol)target;
                            if (!result._registrations.TryGetValue(namedTarget.OriginalDefinition, out var registrations))
                            {
                                result._registrations.Add(namedTarget.OriginalDefinition, registrations = []);
                            }

                            registrations.Add(registration);
                        }
                    }
                }
            }
        }

        return result;
    }

    public bool TryGetSupport(ITypeSymbol type, Requirement requirement, out bool inspectArguments,
        out ITypeSymbol? surrogate, out bool ambiguous)
    {
        var candidates = new List<SupportCandidate>();
        if (type is INamedTypeSymbol named)
        {
            AddMatches(named, false);
            if (requirement == Requirement.Field && named.IsReferenceType)
            {
                for (var baseType = named.BaseType; baseType is not null; baseType = baseType.BaseType)
                {
                    AddMatches(baseType, true);
                }
            }
        }
        else if (type is IArrayTypeSymbol array)
        {
            foreach (var registration in _arrayRegistrations)
            {
                AddCandidate(registration, array, false);
            }
        }

        if (candidates.Count > 0)
        {
            var best = candidates.Min(static candidate => candidate.Priority);
            var bestIsExact = candidates.Any(candidate => candidate.Priority == best && candidate.IsExact);
            var selected = candidates.First(candidate => candidate.Priority == best && candidate.IsExact == bestIsExact);
            ambiguous = selected.Registration.Kind == RegistrationKind.Converter
                && candidates.Any(candidate => candidate.Priority == best && candidate.IsExact == bestIsExact
                    && candidate.Registration.Kind == RegistrationKind.Converter
                    && !SymbolEqualityComparer.Default.Equals(candidate.Surrogate, selected.Surrogate));
            inspectArguments = selected.Registration.InspectArguments;
            surrogate = ambiguous ? null : selected.Surrogate;
            return true;
        }

        inspectArguments = false;
        surrogate = null;
        ambiguous = false;
        return false;

        void AddMatches(INamedTypeSymbol candidateType, bool derivedType)
        {
            if (!_registrations.TryGetValue(candidateType.OriginalDefinition, out var registrations)) return;
            foreach (var registration in registrations)
            {
                if (derivedType && (registration.Kind != RegistrationKind.FieldCodec || !registration.SupportsDerivedTypes))
                {
                    continue;
                }

                AddCandidate(registration, candidateType, derivedType);
            }
        }

        void AddCandidate(Registration registration, ITypeSymbol candidateType, bool derivedType)
        {
            if (!Supports(registration.Kind, requirement, type.IsValueType)) return;
            var arguments = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            if (!Matches(registration.Target, candidateType, arguments)) return;
            // Do not infer support from a constrained open registration for an ineligible construction.
            if (GetTypeParameters(registration.Implementation).Any(parameter =>
                !arguments.TryGetValue(parameter, out var argument) || !SatisfiesConstraints(parameter, argument))) return;
            candidates.Add(new(registration, Priority(registration.Kind, requirement, derivedType),
                SymbolEqualityComparer.Default.Equals(registration.Target, candidateType),
                Substitute(registration.Surrogate, arguments)));
        }
    }

    private static bool Supports(RegistrationKind kind, Requirement requirement, bool isValueType) => requirement switch
    {
        Requirement.Field => true,
        Requirement.Base => kind == RegistrationKind.BaseCodec || kind == RegistrationKind.Converter && !isValueType,
        Requirement.Value => kind == RegistrationKind.ValueSerializer || kind == RegistrationKind.Converter && isValueType,
        _ => false
    };

    private static int Priority(RegistrationKind kind, Requirement requirement, bool derivedType) =>
        (requirement, derivedType, kind) switch
        {
            (_, true, _) => 4,
            (Requirement.Field, false, RegistrationKind.FieldCodec) => 0,
            (Requirement.Field, false, RegistrationKind.BaseCodec) => 1,
            (Requirement.Field, false, RegistrationKind.ValueSerializer) => 2,
            (Requirement.Field, false, RegistrationKind.Converter) => 3,
            (Requirement.Base, false, RegistrationKind.BaseCodec) => 0,
            (Requirement.Base, false, RegistrationKind.Converter) => 1,
            (Requirement.Value, false, RegistrationKind.ValueSerializer) => 0,
            (Requirement.Value, false, RegistrationKind.Converter) => 1,
            _ => int.MaxValue
        };

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

    private enum RegistrationKind
    {
        FieldCodec,
        BaseCodec,
        ValueSerializer,
        Converter
    }

    private sealed record Registration(ITypeSymbol Target, ITypeSymbol? Surrogate, bool InspectArguments,
        INamedTypeSymbol Implementation, RegistrationKind Kind, bool SupportsDerivedTypes);

    private sealed record SupportCandidate(Registration Registration, int Priority, bool IsExact, ITypeSymbol? Surrogate);
}
