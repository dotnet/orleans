using System.Collections.Immutable;
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
    private readonly List<Registration> _parameterRegistrations = [];
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
        var arrayCodec = compilation.GetTypeByMetadataName("Orleans.Serialization.Codecs.ArrayCodec`1");
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
                        && target is INamedTypeSymbol or IArrayTypeSymbol or ITypeParameterSymbol)
                    {
                        if (registrationKind == RegistrationKind.FieldCodec && target is IArrayTypeSymbol
                            && arrayCodec is not null && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, arrayCodec))
                        {
                            continue;
                        }

                        var registration = new Registration(target,
                            registrationKind == RegistrationKind.Converter ? implemented.TypeArguments[1] : null,
                            assembly.Name == "Orleans.Serialization"
                                ? GetInspectedTypeParameters(type, fieldCodec, baseCodec, valueSerializer)
                                : ImmutableArray<ITypeParameterSymbol>.Empty,
                            type, registrationKind,
                            supportsDerivedTypes && registrationKind == RegistrationKind.FieldCodec);
                        if (target is IArrayTypeSymbol)
                        {
                            result._arrayRegistrations.Add(registration);
                        }
                        else if (target is ITypeParameterSymbol)
                        {
                            result._parameterRegistrations.Add(registration);
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

    public bool TryGetSupport(ITypeSymbol type, Requirement requirement, out ImmutableArray<ITypeSymbol> inspectArguments,
        out ITypeSymbol? surrogate, out bool ambiguous)
    {
        var candidates = new List<SupportCandidate>();
        if (type is INamedTypeSymbol named)
        {
            AddMatches(named, false);
            foreach (var registration in _parameterRegistrations)
            {
                AddCandidate(registration, named, false);
            }

            if (requirement == Requirement.Field && named.IsReferenceType)
            {
                for (var baseType = named.BaseType; baseType is not null; baseType = baseType.BaseType)
                {
                    if (AddMatches(baseType, true))
                    {
                        break;
                    }
                }
            }
        }
        else if (type is IArrayTypeSymbol array)
        {
            foreach (var registration in _arrayRegistrations)
            {
                AddCandidate(registration, array, false);
            }

            foreach (var registration in _parameterRegistrations)
            {
                AddCandidate(registration, array, false);
            }
        }

        if (candidates.Count > 0)
        {
            var best = candidates.Min(static candidate => candidate.Priority);
            var bestSpecificity = candidates.Where(candidate => candidate.Priority == best)
                .Min(static candidate => candidate.TargetSpecificity);
            var selected = candidates.First(candidate => candidate.Priority == best
                && candidate.TargetSpecificity == bestSpecificity);
            ambiguous = selected.Registration.Kind == RegistrationKind.Converter
                && candidates.Any(candidate => candidate.Priority == best && candidate.TargetSpecificity == bestSpecificity
                    && candidate.Registration.Kind == RegistrationKind.Converter
                    && !SymbolEqualityComparer.Default.Equals(candidate.Surrogate, selected.Surrogate));
            inspectArguments = selected.InspectedArguments;
            surrogate = ambiguous ? null : selected.Surrogate;
            return true;
        }

        inspectArguments = ImmutableArray<ITypeSymbol>.Empty;
        surrogate = null;
        ambiguous = false;
        return false;

        bool AddMatches(INamedTypeSymbol candidateType, bool derivedType)
        {
            if (!_registrations.TryGetValue(candidateType.OriginalDefinition, out var registrations)) return false;

            var matches = new List<SupportCandidate>();
            foreach (var registration in registrations)
            {
                if (TryCreateCandidate(registration, candidateType, false, out var candidate))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 0) return false;
            if (!derivedType)
            {
                candidates.AddRange(matches);
                return true;
            }

            // The runtime selects a codec for the nearest constructed base before checking whether it supports derived values.
            // An exact registration without IDerivedTypeCodec therefore blocks a less-specific open registration.
            var bestPriority = matches.Min(static candidate => candidate.Priority);
            var bestSpecificity = matches.Where(candidate => candidate.Priority == bestPriority)
                .Min(static candidate => candidate.TargetSpecificity);
            var selected = matches.First(candidate => candidate.Priority == bestPriority
                && candidate.TargetSpecificity == bestSpecificity);
            if (selected.Registration.SupportsDerivedTypes)
            {
                candidates.Add(selected with { Priority = Priority(selected.Registration.Kind, requirement, true) });
            }

            return true;
        }

        void AddCandidate(Registration registration, ITypeSymbol candidateType, bool derivedType)
        {
            if (TryCreateCandidate(registration, candidateType, derivedType, out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        bool TryCreateCandidate(Registration registration, ITypeSymbol candidateType, bool derivedType, out SupportCandidate candidate)
        {
            if (!Supports(registration.Kind, requirement, type.IsValueType)
                || requirement == Requirement.Field && type.TypeKind == TypeKind.Enum && registration.Kind == RegistrationKind.Converter)
            {
                candidate = null!;
                return false;
            }

            var arguments = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            if (!Matches(registration.Target, candidateType, arguments))
            {
                candidate = null!;
                return false;
            }

            // Do not infer support from a constrained open registration for an ineligible construction.
            if (GetTypeParameters(registration.Implementation).Any(parameter =>
                !arguments.TryGetValue(parameter, out var argument) || !SatisfiesConstraints(parameter, argument)))
            {
                candidate = null!;
                return false;
            }

            var targetSpecificity = SymbolEqualityComparer.Default.Equals(registration.Target, candidateType) ? 0
                : registration.Target is ITypeParameterSymbol ? 2
                : 1;
            var inspectedArguments = registration.InspectedTypeParameters
                .Where(arguments.ContainsKey)
                .Select(parameter => arguments[parameter])
                .ToImmutableArray();
            candidate = new(registration, Priority(registration.Kind, requirement, derivedType),
                targetSpecificity, Substitute(registration.Surrogate, arguments), inspectedArguments);
            return true;
        }
    }

    private static ImmutableArray<ITypeParameterSymbol> GetInspectedTypeParameters(INamedTypeSymbol implementation,
        INamedTypeSymbol? fieldCodec, INamedTypeSymbol? baseCodec, INamedTypeSymbol? valueSerializer)
    {
        // Inspect only arguments used by injected serializer services; comparer codecs, for example, carry T without serializing it.
        var result = ImmutableArray.CreateBuilder<ITypeParameterSymbol>();
        var seen = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
        var implementationParameters = new HashSet<ITypeParameterSymbol>(GetTypeParameters(implementation), SymbolEqualityComparer.Default);
        foreach (var constructor in implementation.InstanceConstructors)
        {
            foreach (var parameter in constructor.Parameters)
            {
                AddDependencies(parameter.Type, false);
            }
        }

        return result.ToImmutable();

        void AddDependencies(ITypeSymbol type, bool isDependency)
        {
            if (type is ITypeParameterSymbol parameter)
            {
                if (isDependency && implementationParameters.Contains(parameter) && seen.Add(parameter))
                {
                    result.Add(parameter);
                }

                return;
            }

            if (type is IArrayTypeSymbol array)
            {
                AddDependencies(array.ElementType, isDependency);
                return;
            }

            if (type is not INamedTypeSymbol named)
            {
                return;
            }

            var isCodecDependency = fieldCodec is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, fieldCodec)
                || baseCodec is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, baseCodec)
                || valueSerializer is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, valueSerializer);
            foreach (var argument in named.TypeArguments)
            {
                AddDependencies(argument, isDependency || isCodecDependency);
            }

            if (named.ContainingType is { } containing)
            {
                AddDependencies(containing, isDependency || isCodecDependency);
            }
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

    private sealed record Registration(ITypeSymbol Target, ITypeSymbol? Surrogate, ImmutableArray<ITypeParameterSymbol> InspectedTypeParameters,
        INamedTypeSymbol Implementation, RegistrationKind Kind, bool SupportsDerivedTypes);

    private sealed record SupportCandidate(Registration Registration, int Priority, int TargetSpecificity, ITypeSymbol? Surrogate,
        ImmutableArray<ITypeSymbol> InspectedArguments);
}
