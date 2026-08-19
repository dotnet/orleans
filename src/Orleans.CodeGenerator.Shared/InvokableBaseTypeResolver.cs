using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator;

internal sealed class InvokableBaseTypeResolver
{
    internal const string InvokableBaseTypeAttributeMetadataName = "Orleans.InvokableBaseTypeAttribute";
    internal const string DefaultInvokableBaseTypeAttributeMetadataName = "Orleans.DefaultInvokableBaseTypeAttribute";
    internal const string ReturnValueProxyAttributeMetadataName = "Orleans.Invocation.ReturnValueProxyAttribute";

    private static readonly ConditionalWeakTable<Compilation, Discovery> Discoveries = new();
    private readonly Compilation _compilation;
    private readonly Discovery _discovery;

    public InvokableBaseTypeResolver(Compilation compilation)
    {
        _compilation = compilation;
        _discovery = Discoveries.GetValue(compilation, static value => Discovery.Create(value));
    }

    public bool TryResolve(
        INamedTypeSymbol proxyBaseType,
        IMethodSymbol method,
        out INamedTypeSymbol? invokableBaseType,
        out ResolverDiagnostic? diagnostic)
    {
        var returnType = method.ReturnType;
        foreach (var source in GetCandidateGroups(proxyBaseType, method, returnType))
        {
            var matching = GetBestMatches(source.Mappings, proxyBaseType, returnType);
            if (matching.Count == 0)
            {
                continue;
            }

            if (!TryCoalesce(matching, out var mapping, out diagnostic))
            {
                invokableBaseType = null;
                return false;
            }

            if (source.Kind == MappingKind.Assembly
                && IsBuiltInReplacement(proxyBaseType, returnType, mapping!, out var defaultMapping))
            {
                invokableBaseType = null;
                diagnostic = CreateDiagnostic(
                    mapping!,
                    $"Assembly registration for return type '{Display(returnType)}' cannot replace proxy default '{Display(defaultMapping!.InvokableBaseType)}' with '{Display(mapping!.InvokableBaseType)}'.");
                return false;
            }

            if (!TryConstructAndValidate(proxyBaseType, returnType, mapping!, out invokableBaseType, out diagnostic))
            {
                return false;
            }

            return true;
        }

        invokableBaseType = null;
        diagnostic = new ResolverDiagnostic(
            $"No invokable base type is registered for return type '{Display(returnType)}' and proxy base '{Display(proxyBaseType)}'.",
            method.Locations.FirstOrDefault());
        return false;
    }

    public ImmutableArray<ResolvedMapping> GetMappingsForProxy(INamedTypeSymbol proxyBaseType)
    {
        var result = new Dictionary<string, ResolvedMapping>(StringComparer.Ordinal);
        Add(_discovery.AssemblyMappings);
        Add(GetDefaultMappings(proxyBaseType));
        return [.. result.Values.OrderBy(static value => value.ReturnTypeName, StringComparer.Ordinal)];

        void Add(ImmutableArray<Mapping> mappings)
        {
            foreach (var mapping in mappings)
            {
                if (!SymbolEqualityComparer.Default.Equals(mapping.ProxyBaseType.OriginalDefinition, proxyBaseType.OriginalDefinition))
                {
                    continue;
                }

                var key = Display(mapping.ReturnType);
                if (!result.ContainsKey(key))
                {
                    result.Add(key, new ResolvedMapping(
                        mapping.ReturnType,
                        mapping.InvokableBaseType,
                        Display(mapping.ReturnType),
                        Display(mapping.InvokableBaseType)));
                }
            }
        }
    }

    private IEnumerable<CandidateGroup> GetCandidateGroups(INamedTypeSymbol proxyBaseType, IMethodSymbol method, ITypeSymbol returnType)
    {
        yield return new(MappingKind.Method, GetMethodMappings(method));
        yield return new(MappingKind.ReturnType, GetReturnTypeMappings(returnType));
        yield return new(MappingKind.Assembly, _discovery.AssemblyMappings);
        yield return new(MappingKind.Default, GetDefaultMappings(proxyBaseType));
    }

    private ImmutableArray<Mapping> GetMethodMappings(IMethodSymbol method)
    {
        var builder = ImmutableArray.CreateBuilder<Mapping>();
        foreach (var appliedAttribute in method.GetAttributes())
        {
            if (appliedAttribute.AttributeClass is { } attributeClass)
            {
                AddMappings(attributeClass.GetAttributes(), MappingKind.Method, builder);
            }
        }

        return Sort(builder);
    }

    private ImmutableArray<Mapping> GetReturnTypeMappings(ITypeSymbol returnType)
    {
        if (returnType is not INamedTypeSymbol named)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<Mapping>();
        AddMappings(named.OriginalDefinition.GetAttributes(), MappingKind.ReturnType, builder);
        return Sort(builder);
    }

    private ImmutableArray<Mapping> GetDefaultMappings(INamedTypeSymbol proxyBaseType)
    {
        var attributeType = _compilation.GetTypeByMetadataName(DefaultInvokableBaseTypeAttributeMetadataName);
        if (attributeType is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<Mapping>();
        foreach (var attribute in proxyBaseType.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType)
                || attribute.ConstructorArguments.Length < 2
                || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol returnType
                || attribute.ConstructorArguments[1].Value is not INamedTypeSymbol invokableBaseType)
            {
                continue;
            }

            builder.Add(new Mapping(
                proxyBaseType,
                returnType,
                invokableBaseType,
                MappingKind.Default,
                GetLocation(attribute),
                GetOrigin(attribute, proxyBaseType.ContainingAssembly)));
        }

        return Sort(builder);
    }

    private void AddMappings(
        ImmutableArray<AttributeData> attributes,
        MappingKind kind,
        ImmutableArray<Mapping>.Builder builder)
    {
        var attributeType = _compilation.GetTypeByMetadataName(InvokableBaseTypeAttributeMetadataName);
        if (attributeType is null)
        {
            return;
        }

        foreach (var attribute in attributes)
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType)
                || attribute.ConstructorArguments.Length < 3
                || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol proxyBaseType
                || attribute.ConstructorArguments[1].Value is not INamedTypeSymbol returnType
                || attribute.ConstructorArguments[2].Value is not INamedTypeSymbol invokableBaseType)
            {
                continue;
            }

            builder.Add(new Mapping(
                proxyBaseType,
                returnType,
                invokableBaseType,
                kind,
                GetLocation(attribute),
                GetOrigin(attribute, attribute.AttributeClass?.ContainingAssembly)));
        }
    }

    private static List<Mapping> GetBestMatches(ImmutableArray<Mapping> mappings, INamedTypeSymbol proxyBaseType, ITypeSymbol returnType)
    {
        var exact = new List<Mapping>();
        var open = new List<Mapping>();
        foreach (var mapping in mappings)
        {
            if (!SymbolEqualityComparer.Default.Equals(mapping.ProxyBaseType.OriginalDefinition, proxyBaseType.OriginalDefinition))
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(mapping.ReturnType, returnType))
            {
                exact.Add(mapping);
            }
            else if (returnType is INamedTypeSymbol namedReturnType
                && namedReturnType.IsGenericType
                && SymbolEqualityComparer.Default.Equals(mapping.ReturnType.OriginalDefinition, namedReturnType.OriginalDefinition))
            {
                open.Add(mapping);
            }
        }

        return exact.Count > 0 ? exact : open;
    }

    private static bool TryCoalesce(List<Mapping> mappings, out Mapping? result, out ResolverDiagnostic? diagnostic)
    {
        mappings.Sort(MappingComparer.Instance);
        result = mappings[0];
        var distinct = new List<INamedTypeSymbol>();
        foreach (var mapping in mappings)
        {
            if (!distinct.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, mapping.InvokableBaseType)))
            {
                distinct.Add(mapping.InvokableBaseType);
            }
        }

        distinct.Sort(static (left, right) => StringComparer.Ordinal.Compare(DisplayWithAssembly(left), DisplayWithAssembly(right)));
        if (distinct.Count == 1)
        {
            diagnostic = null;
            return true;
        }

        diagnostic = CreateDiagnostic(
            result,
            $"Conflicting invokable base type registrations for return type '{Display(result.ReturnType)}' and proxy base '{Display(result.ProxyBaseType)}': {string.Join(", ", distinct.Select(DisplayWithAssembly))}.");
        return false;
    }

    private bool IsBuiltInReplacement(INamedTypeSymbol proxyBaseType, ITypeSymbol returnType, Mapping mapping, out Mapping? defaultMapping)
    {
        var defaults = GetBestMatches(GetDefaultMappings(proxyBaseType), proxyBaseType, returnType);
        if (defaults.Count == 0)
        {
            defaultMapping = null;
            return false;
        }

        defaults.Sort(MappingComparer.Instance);
        defaultMapping = defaults[0];
        return !SymbolEqualityComparer.Default.Equals(defaultMapping.InvokableBaseType, mapping.InvokableBaseType);
    }

    private bool TryConstructAndValidate(
        INamedTypeSymbol proxyBaseType,
        ITypeSymbol returnType,
        Mapping mapping,
        out INamedTypeSymbol? result,
        out ResolverDiagnostic? diagnostic)
    {
        var baseType = mapping.InvokableBaseType;
        var returnArity = returnType is INamedTypeSymbol { IsGenericType: true } namedReturn ? namedReturn.Arity : 0;
        var baseArity = baseType.Arity;
        var isOpenMapping = mapping.ReturnType.IsUnboundGenericType
            || mapping.ReturnType.IsGenericType && SymbolEqualityComparer.Default.Equals(mapping.ReturnType, mapping.ReturnType.OriginalDefinition);
        var requiresConstruction = baseType.IsUnboundGenericType;

        if (isOpenMapping && !baseType.IsUnboundGenericType)
        {
            result = null;
            diagnostic = CreateDiagnostic(
                mapping,
                $"Open generic return type mapping '{Display(mapping.ReturnType)}' requires an unbound generic invokable base type, but '{Display(baseType)}' is closed.");
            return false;
        }

        if ((isOpenMapping || requiresConstruction) && returnArity != baseArity)
        {
            result = null;
            diagnostic = CreateDiagnostic(
                mapping,
                $"Invokable base type '{Display(baseType)}' has arity {baseArity}, but return type '{Display(mapping.ReturnType)}' has arity {returnArity}.");
            return false;
        }

        if (baseType.TypeKind != TypeKind.Class || baseType.IsSealed || baseType.IsStatic)
        {
            result = null;
            diagnostic = CreateDiagnostic(mapping, $"Invokable base type '{Display(baseType)}' must be a non-sealed, non-static class.");
            return false;
        }

        if (!_compilation.IsSymbolAccessibleWithin(baseType, _compilation.Assembly))
        {
            result = null;
            diagnostic = CreateDiagnostic(mapping, $"Invokable base type '{Display(baseType)}' is not accessible from the generated proxy assembly.");
            return false;
        }

        result = baseType;
        if ((isOpenMapping || requiresConstruction) && returnType is INamedTypeSymbol { IsGenericType: true } constructedReturn)
        {
            if (!SatisfiesConstraints(baseType.OriginalDefinition, constructedReturn.TypeArguments, out var reason))
            {
                result = null;
                diagnostic = CreateDiagnostic(mapping, reason);
                return false;
            }

            result = baseType.OriginalDefinition.Construct([.. constructedReturn.TypeArguments]);
        }

        if (!ValidateInitializer(proxyBaseType, returnType, result, mapping, out diagnostic))
        {
            result = null;
            return false;
        }

        return true;
    }

    private bool SatisfiesConstraints(INamedTypeSymbol baseType, ImmutableArray<ITypeSymbol> typeArguments, out string reason)
    {
        for (var i = 0; i < baseType.TypeParameters.Length; i++)
        {
            var parameter = baseType.TypeParameters[i];
            var argument = typeArguments[i];
            if (!SatisfiesSpecialConstraints(parameter, argument))
            {
                reason = $"Type argument '{Display(argument)}' does not satisfy the constraints for '{parameter.Name}' on invokable base type '{Display(baseType)}'.";
                return false;
            }

            if (parameter.HasConstructorConstraint && !SatisfiesConstructorConstraint(argument))
            {
                reason = $"Type argument '{Display(argument)}' does not satisfy the constructor constraint for '{parameter.Name}' on invokable base type '{Display(baseType)}'.";
                return false;
            }

            foreach (var constraint in parameter.ConstraintTypes)
            {
                var substitutedConstraint = SubstituteConstraint(constraint, baseType.TypeParameters, typeArguments);
                if (!_compilation.ClassifyCommonConversion(argument, substitutedConstraint).IsImplicit)
                {
                    reason = $"Type argument '{Display(argument)}' does not satisfy constraint '{Display(substitutedConstraint)}' for '{parameter.Name}' on invokable base type '{Display(baseType)}'.";
                    return false;
                }
            }
        }

        reason = string.Empty;
        return true;

        static bool SatisfiesSpecialConstraints(ITypeParameterSymbol parameter, ITypeSymbol argument)
        {
            var argumentParameter = argument as ITypeParameterSymbol;
            var isNullableValueType = IsNullableValueType(argument);

            if (parameter.HasReferenceTypeConstraint)
            {
                var isKnownReferenceType = argument.IsReferenceType
                    || argumentParameter?.HasReferenceTypeConstraint == true;
                if (!isKnownReferenceType)
                {
                    return false;
                }

                if (parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.NotAnnotated
                    && (argument.NullableAnnotation == NullableAnnotation.Annotated
                        || argumentParameter?.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated))
                {
                    return false;
                }
            }

            if (parameter.HasValueTypeConstraint
                && (!argument.IsValueType && argumentParameter?.HasValueTypeConstraint != true
                    || isNullableValueType))
            {
                return false;
            }

            if (parameter.HasUnmanagedTypeConstraint
                && (!argument.IsUnmanagedType && argumentParameter?.HasUnmanagedTypeConstraint != true))
            {
                return false;
            }

            if (parameter.HasNotNullConstraint
                && (isNullableValueType
                    || argument.NullableAnnotation == NullableAnnotation.Annotated
                    || argumentParameter is not null && !HasNotNullConstraint(argumentParameter)))
            {
                return false;
            }

            return !argument.IsRefLikeType;

            static bool HasNotNullConstraint(ITypeParameterSymbol typeParameter)
                => typeParameter.HasNotNullConstraint
                    || typeParameter.HasValueTypeConstraint
                    || typeParameter.HasUnmanagedTypeConstraint
                    || typeParameter.HasReferenceTypeConstraint
                        && typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.NotAnnotated;
        }

        static bool SatisfiesConstructorConstraint(ITypeSymbol argument)
        {
            if (argument is ITypeParameterSymbol typeParameter)
            {
                return typeParameter.HasConstructorConstraint
                    || typeParameter.HasValueTypeConstraint
                    || typeParameter.HasUnmanagedTypeConstraint;
            }

            if (argument.IsValueType)
            {
                return true;
            }

            return argument is INamedTypeSymbol { IsAbstract: false } namedArgument
                && namedArgument.InstanceConstructors.Any(static constructor =>
                    constructor.Parameters.Length == 0
                    && constructor.DeclaredAccessibility == Accessibility.Public);
        }

        static bool IsNullableValueType(ITypeSymbol argument)
            => argument is INamedTypeSymbol namedArgument
                && namedArgument.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

        static ITypeSymbol SubstituteConstraint(
            ITypeSymbol constraint,
            ImmutableArray<ITypeParameterSymbol> parameters,
            ImmutableArray<ITypeSymbol> arguments)
        {
            if (constraint is ITypeParameterSymbol typeParameter
                && SymbolEqualityComparer.Default.Equals(typeParameter.ContainingSymbol, parameters[0].ContainingSymbol))
            {
                return arguments[typeParameter.Ordinal];
            }

            if (constraint is INamedTypeSymbol { IsGenericType: true } namedConstraint)
            {
                var substitutedArguments = namedConstraint.TypeArguments
                    .Select(argument => SubstituteConstraint(argument, parameters, arguments))
                    .ToArray();
                return namedConstraint.OriginalDefinition.Construct(substitutedArguments);
            }

            return constraint;
        }
    }

    private bool ValidateInitializer(
        INamedTypeSymbol proxyBaseType,
        ITypeSymbol returnType,
        INamedTypeSymbol baseType,
        Mapping mapping,
        out ResolverDiagnostic? diagnostic)
    {
        var returnValueProxyAttribute = _compilation.GetTypeByMetadataName(ReturnValueProxyAttributeMetadataName);
        var attribute = returnValueProxyAttribute is null
            ? null
            : baseType.GetAttributes().FirstOrDefault(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, returnValueProxyAttribute));
        if (attribute is null)
        {
            diagnostic = null;
            return true;
        }

        if (attribute.ConstructorArguments.Length == 0
            || attribute.ConstructorArguments[0].Value is not string methodName
            || string.IsNullOrWhiteSpace(methodName))
        {
            diagnostic = CreateDiagnostic(mapping, $"Return-value proxy initializer on '{Display(baseType)}' does not specify a method name.");
            return false;
        }

        for (var current = baseType; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(methodName).OfType<IMethodSymbol>())
            {
                if (member.IsStatic || member.IsAbstract || member.Arity != 0
                    || member.Parameters.Length != 1 || member.Parameters[0].RefKind != RefKind.None
                    || !_compilation.IsSymbolAccessibleWithin(member, _compilation.Assembly))
                {
                    continue;
                }

                var proxyConversion = _compilation.ClassifyCommonConversion(proxyBaseType, member.Parameters[0].Type);
                var returnConversion = _compilation.ClassifyCommonConversion(member.ReturnType, returnType);
                if (proxyConversion.IsImplicit && returnConversion.IsImplicit)
                {
                    diagnostic = null;
                    return true;
                }
            }
        }

        diagnostic = CreateDiagnostic(
            mapping,
            $"Return-value proxy initializer '{Display(baseType)}.{methodName}' must be an accessible, concrete, non-generic instance method with one by-value parameter accepting '{Display(proxyBaseType)}' and a return type assignable to '{Display(returnType)}'.");
        return false;
    }

    private static ResolverDiagnostic CreateDiagnostic(Mapping mapping, string message) => new(message, mapping.Location);
    private static string Display(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
    private static string DisplayWithAssembly(INamedTypeSymbol type)
        => $"{Display(type)} [{type.ContainingAssembly.Identity}]";
    private static Location? GetLocation(AttributeData attribute) => attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
    private static string GetOrigin(AttributeData attribute, IAssemblySymbol? assembly)
        => $"{assembly?.Identity}|{attribute.ApplicationSyntaxReference?.SyntaxTree.FilePath}|{attribute.ApplicationSyntaxReference?.Span.Start ?? -1}";

    private static ImmutableArray<Mapping> Sort(ImmutableArray<Mapping>.Builder builder)
        => [.. builder.OrderBy(static mapping => mapping, MappingComparer.Instance)];

    private readonly record struct CandidateGroup(MappingKind Kind, ImmutableArray<Mapping> Mappings);

    private sealed record Mapping(
        INamedTypeSymbol ProxyBaseType,
        INamedTypeSymbol ReturnType,
        INamedTypeSymbol InvokableBaseType,
        MappingKind Kind,
        Location? Location,
        string Origin);

    private enum MappingKind
    {
        Method,
        ReturnType,
        Assembly,
        Default,
    }

    private sealed class MappingComparer : IComparer<Mapping>
    {
        public static MappingComparer Instance { get; } = new();

        public int Compare(Mapping? x, Mapping? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var result = StringComparer.Ordinal.Compare(Display(x.ReturnType), Display(y.ReturnType));
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(Display(x.InvokableBaseType), Display(y.InvokableBaseType));
            if (result != 0) return result;
            return StringComparer.Ordinal.Compare(x.Origin, y.Origin);
        }
    }

    private sealed class Discovery
    {
        private Discovery(ImmutableArray<Mapping> assemblyMappings) => AssemblyMappings = assemblyMappings;

        public ImmutableArray<Mapping> AssemblyMappings { get; }

        public static Discovery Create(Compilation compilation)
        {
            var attributeType = compilation.GetTypeByMetadataName(InvokableBaseTypeAttributeMetadataName);
            if (attributeType is null)
            {
                return new([]);
            }

            var assemblies = compilation.SourceModule.ReferencedAssemblySymbols
                .Append(compilation.Assembly)
                .GroupBy(static assembly => assembly.Identity.ToString(), StringComparer.Ordinal)
                .Select(static group => group.First())
                .OrderBy(static assembly => assembly.Identity.ToString(), StringComparer.Ordinal);
            var builder = ImmutableArray.CreateBuilder<Mapping>();
            foreach (var assembly in assemblies)
            {
                foreach (var attribute in assembly.GetAttributes())
                {
                    if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType)
                        || attribute.ConstructorArguments.Length < 3
                        || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol proxyBaseType
                        || attribute.ConstructorArguments[1].Value is not INamedTypeSymbol returnType
                        || attribute.ConstructorArguments[2].Value is not INamedTypeSymbol invokableBaseType)
                    {
                        continue;
                    }

                    builder.Add(new Mapping(
                        proxyBaseType,
                        returnType,
                        invokableBaseType,
                        MappingKind.Assembly,
                        GetLocation(attribute),
                        GetOrigin(attribute, assembly)));
                }
            }

            return new(Sort(builder));
        }
    }
}

internal sealed record ResolverDiagnostic(string Message, Location? Location);

internal readonly record struct ResolvedMapping(
    INamedTypeSymbol ReturnType,
    INamedTypeSymbol InvokableBaseType,
    string ReturnTypeName,
    string InvokableBaseTypeName);
