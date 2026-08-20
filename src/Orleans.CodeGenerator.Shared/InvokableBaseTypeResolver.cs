using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator;

internal sealed class InvokableBaseTypeResolver
{
    internal const string InvokableBaseTypeAttributeMetadataName = "Orleans.InvokableBaseTypeAttribute";
    internal const string DefaultInvokableBaseTypeAttributeMetadataName = "Orleans.DefaultInvokableBaseTypeAttribute";
    internal const string ReturnValueProxyAttributeMetadataName = "Orleans.Invocation.ReturnValueProxyAttribute";
    internal const string GeneratedActivatorConstructorAttributeMetadataName = "Orleans.GeneratedActivatorConstructorAttribute";

    private static readonly ConditionalWeakTable<Compilation, Lazy<Discovery>> Discoveries = new();
    private static readonly ConditionalWeakTable<Compilation, Lazy<BindingCache>> BindingCaches = new();
    private static readonly ImmutableArray<MappingMatchKind> MappingMatchKinds =
        [MappingMatchKind.Exact, MappingMatchKind.OpenGeneric];
    private readonly Compilation _compilation;
    private readonly Discovery _discovery;
    private readonly BindingCache _bindingCache;
    private readonly Func<INamedTypeSymbol, Lazy<bool>> _constructorBindingFactory;
    private readonly Func<InitializerBindingKey, Lazy<bool>> _initializerBindingFactory;
    private readonly Func<IMethodSymbol, Lazy<ImmutableArray<Mapping>>> _methodMappingsFactory;
    private readonly Func<INamedTypeSymbol, Lazy<ImmutableArray<Mapping>>> _returnTypeMappingsFactory;
    private readonly Func<INamedTypeSymbol, Lazy<ImmutableArray<Mapping>>> _defaultMappingsFactory;
    private readonly Func<INamedTypeSymbol, Lazy<ImmutableArray<ResolvedMapping>>> _proxyMappingsFactory;

    public InvokableBaseTypeResolver(Compilation compilation)
    {
        _compilation = compilation;
        _discovery = Discoveries.GetValue(
            compilation,
            static value => new Lazy<Discovery>(
                () => Discovery.Create(value),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        _bindingCache = BindingCaches.GetValue(
            compilation,
            static value => new Lazy<BindingCache>(
                () => new BindingCache(value),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        _constructorBindingFactory = type => new Lazy<bool>(
            () => HasUsableConstructorCore(type),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _initializerBindingFactory = key => new Lazy<bool>(
            () => TryBindInitializer(
                key.ProxyBaseType,
                key.ProxyInterface,
                key.ReturnType,
                key.BaseType,
                key.MethodName,
                out var isValid)
                && isValid,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _methodMappingsFactory = method => new Lazy<ImmutableArray<Mapping>>(
            () => GetMethodMappingsCore(method),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _returnTypeMappingsFactory = returnType => new Lazy<ImmutableArray<Mapping>>(
            () => GetReturnTypeMappingsCore(returnType),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _defaultMappingsFactory = proxyBaseType => new Lazy<ImmutableArray<Mapping>>(
            () => GetDefaultMappingsCore(proxyBaseType),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _proxyMappingsFactory = proxyBaseType => new Lazy<ImmutableArray<ResolvedMapping>>(
            () => GetMappingsForProxyCore(proxyBaseType),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static bool TryGetProxyBaseType(
        INamedTypeSymbol interfaceType,
        INamedTypeSymbol generateMethodSerializersAttribute,
        [NotNullWhen(true)] out INamedTypeSymbol? proxyBaseType,
        out bool isExtension)
    {
        if (TryGetProxyBaseType(interfaceType.GetAttributes(), generateMethodSerializersAttribute, out proxyBaseType, out isExtension))
        {
            return true;
        }

        foreach (var inheritedInterface in interfaceType.AllInterfaces)
        {
            if (TryGetProxyBaseType(inheritedInterface.GetAttributes(), generateMethodSerializersAttribute, out proxyBaseType, out isExtension))
            {
                return true;
            }
        }

        proxyBaseType = null;
        isExtension = false;
        return false;

        static bool TryGetProxyBaseType(
            ImmutableArray<AttributeData> attributes,
            INamedTypeSymbol generateMethodSerializersAttribute,
            [NotNullWhen(true)] out INamedTypeSymbol? proxyBaseType,
            out bool isExtension)
        {
            foreach (var attribute in attributes)
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, generateMethodSerializersAttribute)
                    || attribute.ConstructorArguments.Length == 0
                    || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol candidate)
                {
                    continue;
                }

                proxyBaseType = candidate.OriginalDefinition;
                isExtension = attribute.ConstructorArguments.Length > 1
                    && attribute.ConstructorArguments[1].Value is true;
                return true;
            }

            proxyBaseType = null;
            isExtension = false;
            return false;
        }
    }

    public bool TryResolve(
        INamedTypeSymbol proxyBaseType,
        IMethodSymbol method,
        out INamedTypeSymbol? invokableBaseType,
        out ResolverDiagnostic? diagnostic)
        => TryResolve(proxyBaseType, method, method.ContainingType, out invokableBaseType, out diagnostic);

    public bool TryResolve(
        INamedTypeSymbol proxyBaseType,
        IMethodSymbol method,
        INamedTypeSymbol proxyInterface,
        out INamedTypeSymbol? invokableBaseType,
        out ResolverDiagnostic? diagnostic)
        => TryResolveCore(proxyBaseType, method, proxyInterface, out invokableBaseType, out diagnostic);

    public bool TryResolveBaseType(
        INamedTypeSymbol proxyBaseType,
        IMethodSymbol method,
        out INamedTypeSymbol? invokableBaseType,
        out ResolverDiagnostic? diagnostic)
        => TryResolveCore(proxyBaseType, method, proxyInterface: null, out invokableBaseType, out diagnostic);

    private bool TryResolveCore(
        INamedTypeSymbol proxyBaseType,
        IMethodSymbol method,
        INamedTypeSymbol? proxyInterface,
        out INamedTypeSymbol? invokableBaseType,
        out ResolverDiagnostic? diagnostic)
    {
        var returnType = method.ReturnType;
        foreach (var matchKind in MappingMatchKinds)
        {
            var status = TryResolveCandidateGroup(
                MappingKind.Method,
                GetMethodMappings(method),
                proxyBaseType,
                proxyInterface,
                method,
                returnType,
                matchKind,
                out invokableBaseType,
                out diagnostic);
            if (status != CandidateResolutionStatus.NoMatch)
            {
                return status == CandidateResolutionStatus.Success;
            }

            status = TryResolveCandidateGroup(
                MappingKind.ReturnType,
                GetReturnTypeMappings(returnType),
                proxyBaseType,
                proxyInterface,
                method,
                returnType,
                matchKind,
                out invokableBaseType,
                out diagnostic);
            if (status != CandidateResolutionStatus.NoMatch)
            {
                return status == CandidateResolutionStatus.Success;
            }

            status = TryResolveCandidateGroup(
                MappingKind.Assembly,
                _discovery.AssemblyMappings,
                proxyBaseType,
                proxyInterface,
                method,
                returnType,
                matchKind,
                out invokableBaseType,
                out diagnostic);
            if (status != CandidateResolutionStatus.NoMatch)
            {
                return status == CandidateResolutionStatus.Success;
            }

            status = TryResolveCandidateGroup(
                MappingKind.Default,
                GetDefaultMappings(proxyBaseType),
                proxyBaseType,
                proxyInterface,
                method,
                returnType,
                matchKind,
                out invokableBaseType,
                out diagnostic);
            if (status != CandidateResolutionStatus.NoMatch)
            {
                return status == CandidateResolutionStatus.Success;
            }
        }

        invokableBaseType = null;
        diagnostic = new ResolverDiagnostic(
            ResolverDiagnosticKind.MissingMapping,
            $"No invokable base type is registered for return type '{Display(returnType)}' and proxy base '{Display(proxyBaseType)}'.",
            method.Locations.FirstOrDefault());
        return false;
    }

    public ImmutableArray<ResolvedMapping> GetMappingsForProxy(INamedTypeSymbol proxyBaseType)
        => _bindingCache.ProxyMappings.GetOrAdd(
            proxyBaseType.OriginalDefinition,
            _proxyMappingsFactory).Value;

    private ImmutableArray<ResolvedMapping> GetMappingsForProxyCore(INamedTypeSymbol proxyBaseType)
    {
        var result = new Dictionary<INamedTypeSymbol, ResolvedMapping>(SymbolEqualityComparer.Default);
        Add(_discovery.AssemblyMappings);
        Add(GetDefaultMappings(proxyBaseType));
        return [.. result.Values
            .OrderBy(static value => value.ReturnTypeName, StringComparer.Ordinal)
            .ThenBy(static value => value.ReturnType.ContainingAssembly.Identity.GetDisplayName(), StringComparer.Ordinal)
            .ThenBy(static value => value.InvokableBaseTypeName, StringComparer.Ordinal)
            .ThenBy(static value => value.InvokableBaseType.ContainingAssembly.Identity.GetDisplayName(), StringComparer.Ordinal)];

        void Add(ImmutableArray<Mapping> mappings)
        {
            foreach (var mapping in mappings)
            {
                if (!SymbolEqualityComparer.Default.Equals(mapping.ProxyBaseType.OriginalDefinition, proxyBaseType.OriginalDefinition))
                {
                    continue;
                }

                if (!result.ContainsKey(mapping.ReturnType))
                {
                    result.Add(mapping.ReturnType, new ResolvedMapping(
                        mapping.ReturnType,
                        mapping.InvokableBaseType,
                        Display(mapping.ReturnType),
                        Display(mapping.InvokableBaseType)));
                }
            }
        }
    }

    private CandidateResolutionStatus TryResolveCandidateGroup(
        MappingKind kind,
        ImmutableArray<Mapping> mappings,
        INamedTypeSymbol proxyBaseType,
        INamedTypeSymbol? proxyInterface,
        IMethodSymbol method,
        ITypeSymbol returnType,
        MappingMatchKind matchKind,
        out INamedTypeSymbol? invokableBaseType,
        out ResolverDiagnostic? diagnostic)
    {
        var matching = GetMatches(mappings, proxyBaseType, returnType, matchKind);
        if (matching is null)
        {
            invokableBaseType = null;
            diagnostic = null;
            return CandidateResolutionStatus.NoMatch;
        }

        if (!TryCoalesce(matching, out var mapping, out diagnostic))
        {
            invokableBaseType = null;
            return CandidateResolutionStatus.Failure;
        }

        if (kind == MappingKind.Assembly
            && IsBuiltInReplacement(proxyBaseType, returnType, mapping!, out var defaultMapping))
        {
            invokableBaseType = null;
            diagnostic = CreateDiagnostic(
                mapping!,
                $"Assembly registration for return type '{Display(returnType)}' cannot replace proxy default '{Display(defaultMapping!.InvokableBaseType)}' with '{Display(mapping!.InvokableBaseType)}'.");
            return CandidateResolutionStatus.Failure;
        }

        return TryConstructAndValidate(
            proxyBaseType,
            proxyInterface,
            method,
            returnType,
            mapping!,
            out invokableBaseType,
            out diagnostic)
            ? CandidateResolutionStatus.Success
            : CandidateResolutionStatus.Failure;
    }

    private ImmutableArray<Mapping> GetMethodMappings(IMethodSymbol method)
        => _bindingCache.MethodMappings.GetOrAdd(
            method.OriginalDefinition,
            _methodMappingsFactory).Value;

    private ImmutableArray<Mapping> GetMethodMappingsCore(IMethodSymbol method)
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

        return _bindingCache.ReturnTypeMappings.GetOrAdd(
            named.OriginalDefinition,
            _returnTypeMappingsFactory).Value;
    }

    private ImmutableArray<Mapping> GetReturnTypeMappingsCore(INamedTypeSymbol returnType)
    {
        var builder = ImmutableArray.CreateBuilder<Mapping>();
        AddMappings(returnType.GetAttributes(), MappingKind.ReturnType, builder);
        return Sort(builder);
    }

    private ImmutableArray<Mapping> GetDefaultMappings(INamedTypeSymbol proxyBaseType)
        => _bindingCache.DefaultMappings.GetOrAdd(
            proxyBaseType.OriginalDefinition,
            _defaultMappingsFactory).Value;

    private ImmutableArray<Mapping> GetDefaultMappingsCore(INamedTypeSymbol proxyBaseType)
    {
        var attributeType = _bindingCache.DefaultInvokableBaseTypeAttribute;
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
        var attributeType = _bindingCache.InvokableBaseTypeAttribute;
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

    private static List<Mapping>? GetBestMatches(ImmutableArray<Mapping> mappings, INamedTypeSymbol proxyBaseType, ITypeSymbol returnType)
    {
        var exact = GetMatches(mappings, proxyBaseType, returnType, MappingMatchKind.Exact);
        return exact is not null
            ? exact
            : GetMatches(mappings, proxyBaseType, returnType, MappingMatchKind.OpenGeneric);
    }

    private static List<Mapping>? GetMatches(
        ImmutableArray<Mapping> mappings,
        INamedTypeSymbol proxyBaseType,
        ITypeSymbol returnType,
        MappingMatchKind matchKind)
    {
        List<Mapping>? result = null;
        foreach (var mapping in mappings)
        {
            if (!SymbolEqualityComparer.Default.Equals(mapping.ProxyBaseType.OriginalDefinition, proxyBaseType.OriginalDefinition))
            {
                continue;
            }

            if (matchKind == MappingMatchKind.Exact
                && SymbolEqualityComparer.Default.Equals(mapping.ReturnType, returnType))
            {
                (result ??= []).Add(mapping);
            }
            else if (matchKind == MappingMatchKind.OpenGeneric
                && returnType is INamedTypeSymbol namedReturnType
                && namedReturnType.IsGenericType
                && mapping.ReturnType.IsUnboundGenericType
                && SymbolEqualityComparer.Default.Equals(mapping.ReturnType.OriginalDefinition, namedReturnType.OriginalDefinition))
            {
                (result ??= []).Add(mapping);
            }
        }

        return result;
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
        if (defaults is null)
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
        INamedTypeSymbol? proxyInterface,
        IMethodSymbol method,
        ITypeSymbol returnType,
        Mapping mapping,
        out INamedTypeSymbol? result,
        out ResolverDiagnostic? diagnostic)
    {
        var baseType = mapping.InvokableBaseType;
        var returnArity = returnType is INamedTypeSymbol { IsGenericType: true } namedReturn ? namedReturn.Arity : 0;
        var baseArity = baseType.Arity;
        var isOpenMapping = mapping.ReturnType.IsUnboundGenericType;
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

        if (!HasUsableConstructor(result))
        {
            result = null;
            diagnostic = CreateDiagnostic(
                mapping,
                $"Invokable base type '{Display(baseType)}' must declare an accessible parameterless constructor or an accessible constructor annotated with [GeneratedActivatorConstructor].");
            return false;
        }

        if (proxyInterface is not null
            && !ValidateInitializer(proxyBaseType, proxyInterface, returnType, result, mapping, out diagnostic))
        {
            result = null;
            return false;
        }

        diagnostic = null;
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
                        && typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.NotAnnotated
                    || typeParameter.ConstraintTypes.Any(static constraint =>
                        constraint.NullableAnnotation == NullableAnnotation.NotAnnotated
                        && (constraint.IsReferenceType
                            || constraint is ITypeParameterSymbol constrainedParameter
                                && HasNotNullConstraint(constrainedParameter)));
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

        ITypeSymbol SubstituteConstraint(
            ITypeSymbol constraint,
            ImmutableArray<ITypeParameterSymbol> parameters,
            ImmutableArray<ITypeSymbol> arguments)
        {
            if (constraint is ITypeParameterSymbol typeParameter
                && SymbolEqualityComparer.Default.Equals(typeParameter.ContainingSymbol, parameters[0].ContainingSymbol))
            {
                return arguments[typeParameter.Ordinal];
            }

            if (constraint is IArrayTypeSymbol arrayConstraint)
            {
                return _compilation.CreateArrayTypeSymbol(
                    SubstituteConstraint(arrayConstraint.ElementType, parameters, arguments),
                    arrayConstraint.Rank,
                    arrayConstraint.NullableAnnotation);
            }

            if (constraint is IPointerTypeSymbol pointerConstraint)
            {
                return _compilation.CreatePointerTypeSymbol(
                    SubstituteConstraint(pointerConstraint.PointedAtType, parameters, arguments));
            }

            if (constraint is IFunctionPointerTypeSymbol functionPointerConstraint)
            {
                var signature = functionPointerConstraint.Signature;
                return _compilation.CreateFunctionPointerTypeSymbol(
                    SubstituteConstraint(signature.ReturnType, parameters, arguments),
                    signature.RefKind,
                    [.. signature.Parameters.Select(parameter => SubstituteConstraint(parameter.Type, parameters, arguments))],
                    [.. signature.Parameters.Select(static parameter => parameter.RefKind)],
                    signature.CallingConvention,
                    signature.UnmanagedCallingConventionTypes);
            }

            if (constraint is INamedTypeSymbol namedConstraint)
            {
                var definition = namedConstraint.OriginalDefinition;
                if (namedConstraint.ContainingType is { } containingType)
                {
                    var substitutedContainingType = (INamedTypeSymbol)SubstituteConstraint(containingType, parameters, arguments);
                    definition = substitutedContainingType.GetTypeMembers(namedConstraint.Name, namedConstraint.Arity)
                        .First(candidate => SymbolEqualityComparer.Default.Equals(
                            candidate.OriginalDefinition,
                            namedConstraint.OriginalDefinition));
                }

                if (namedConstraint.Arity > 0)
                {
                    definition = definition.Construct(
                        [.. namedConstraint.TypeArguments.Select(argument => SubstituteConstraint(argument, parameters, arguments))]);
                }

                return definition.WithNullableAnnotation(namedConstraint.NullableAnnotation);
            }

            return constraint;
        }
    }

    private bool HasUsableConstructor(INamedTypeSymbol baseType)
        => _bindingCache.ConstructorBindings.GetOrAdd(baseType, _constructorBindingFactory).Value;

    private bool HasUsableConstructorCore(INamedTypeSymbol baseType)
    {
        var generatedActivatorConstructorAttribute = _bindingCache.GeneratedActivatorConstructorAttribute;
        var generatedActivatorConstructor = generatedActivatorConstructorAttribute is null
            ? null
            : GetConstructorsInGeneratorOrder(baseType).FirstOrDefault(constructor =>
                !constructor.IsImplicitlyDeclared
                && constructor.DeclaredAccessibility != Accessibility.Private
                && constructor.GetAttributes().Any(attribute =>
                    SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, generatedActivatorConstructorAttribute)));

        if (generatedActivatorConstructor is not null)
        {
            return IsAccessibleFromGeneratedDerivedType(generatedActivatorConstructor)
                && generatedActivatorConstructor.Parameters.All(static parameter => parameter.RefKind == RefKind.None)
                && TryBindBaseConstructor(baseType, generatedActivatorConstructor.Parameters, out var canInvoke)
                && canInvoke;
        }

        return TryBindParameterlessBaseConstructor(baseType, out var canInvokeParameterless)
            && canInvokeParameterless;

        IEnumerable<IMethodSymbol> GetConstructorsInGeneratorOrder(INamedTypeSymbol type)
        {
            var baseTypes = new Stack<INamedTypeSymbol>();
            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                baseTypes.Push(current);
            }

            foreach (var current in baseTypes.Append(type))
            {
                foreach (var constructor in current.InstanceConstructors)
                {
                    yield return constructor;
                }
            }
        }

    }

    private bool IsAccessibleFromGeneratedDerivedType(IMethodSymbol constructor)
        => constructor.DeclaredAccessibility switch
        {
            Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal => _compilation.IsSymbolAccessibleWithin(constructor, _compilation.Assembly),
            Accessibility.ProtectedAndInternal => SymbolEqualityComparer.Default.Equals(
                constructor.ContainingAssembly,
                _compilation.Assembly),
            _ => false,
        };

    private bool ValidateInitializer(
        INamedTypeSymbol proxyBaseType,
        INamedTypeSymbol proxyInterface,
        ITypeSymbol returnType,
        INamedTypeSymbol baseType,
        Mapping mapping,
        out ResolverDiagnostic? diagnostic)
    {
        var returnValueProxyAttribute = _bindingCache.ReturnValueProxyAttribute;
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

        var key = new InitializerBindingKey(proxyBaseType, proxyInterface, returnType, baseType, methodName);
        var isValid = _bindingCache.InitializerBindings.GetOrAdd(key, _initializerBindingFactory).Value;
        diagnostic = isValid
            ? null
            : CreateInvalidInitializerDiagnostic(mapping, baseType, proxyBaseType, returnType, methodName);
        return isValid;
    }

    private bool TryBindParameterlessBaseConstructor(INamedTypeSymbol baseType, out bool canInvoke)
    {
        canInvoke = false;
        if (!TryCreateBindingContext(
            [baseType],
            out var compilation,
            out var typeParameters,
            out var constraints,
            out var substitutions))
        {
            return false;
        }

        var source = $$"""
            namespace __OrleansCodeGenSemanticBinding
            {
                internal abstract class __Request{{typeParameters}} : {{DisplayFullyQualified(baseType, substitutions)}}
                    {{constraints}}
                {
                    protected __Request() : base() { }
                }
            }
            """;
        var tree = ParseBindingTree(compilation, source);
        compilation = compilation.AddSyntaxTrees(tree);
        var model = compilation.GetSemanticModel(tree);
        var initializer = tree.GetRoot().DescendantNodes().OfType<ConstructorInitializerSyntax>().Single();
        canInvoke = model.GetSymbolInfo(initializer).Symbol is IMethodSymbol;
        return true;
    }

    private bool TryBindBaseConstructor(
        INamedTypeSymbol baseType,
        ImmutableArray<IParameterSymbol> parameters,
        out bool canInvoke)
    {
        canInvoke = false;
        if (!TryCreateBindingContext(
            parameters.Select(static parameter => parameter.Type).Prepend(baseType),
            out var compilation,
            out var typeParameters,
            out var constraints,
            out var substitutions))
        {
            return false;
        }

        var parameterList = string.Join(
            ", ",
            parameters.Select((parameter, index) =>
                $"{DisplayFullyQualified(parameter.Type, substitutions)} arg{index}"));
        var argumentList = string.Join(", ", parameters.Select(static (_, index) => $"arg{index}"));
        var source = $$"""
            namespace __OrleansCodeGenSemanticBinding
            {
                internal abstract class __Request{{typeParameters}} : {{DisplayFullyQualified(baseType, substitutions)}}
                    {{constraints}}
                {
                    protected __Request({{parameterList}}) : base({{argumentList}}) { }
                }
            }
            """;
        var tree = ParseBindingTree(compilation, source);
        compilation = compilation.AddSyntaxTrees(tree);
        var model = compilation.GetSemanticModel(tree);
        var initializer = tree.GetRoot().DescendantNodes().OfType<ConstructorInitializerSyntax>().Single();
        canInvoke = model.GetSymbolInfo(initializer).Symbol is IMethodSymbol;
        return true;
    }

    private bool TryBindInitializer(
        INamedTypeSymbol proxyBaseType,
        INamedTypeSymbol proxyInterface,
        ITypeSymbol returnType,
        INamedTypeSymbol baseType,
        string methodName,
        out bool isValid)
    {
        isValid = false;
        if (!TryCreateBindingContext(
            [proxyBaseType, proxyInterface, returnType, baseType],
            out var compilation,
            out var typeParameters,
            out var constraints,
            out var substitutions))
        {
            return false;
        }

        var source = $$"""
            namespace __OrleansCodeGenSemanticBinding
            {
                internal abstract class __Proxy{{typeParameters}} :
                    {{DisplayFullyQualified(proxyBaseType, substitutions)}},
                    {{DisplayFullyQualified(proxyInterface, substitutions)}}
                    {{constraints}}
                {
                    private {{DisplayFullyQualified(returnType, substitutions)}} __Bind(
                        __Request{{typeParameters}} request) => request.{{EscapeIdentifier(methodName)}}(this);
                }

                internal abstract class __Request{{typeParameters}} : {{DisplayFullyQualified(baseType, substitutions)}}
                    {{constraints}}
                {
                }
            }
            """;
        var tree = ParseBindingTree(compilation, source);
        compilation = compilation.AddSyntaxTrees(tree);
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var bindMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var selected = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        var boundReturnType = model.GetTypeInfo(bindMethod.ReturnType).Type;
        isValid = selected is
        {
            IsStatic: false,
            IsAbstract: false,
            Arity: 0,
            Parameters.Length: 1,
        }
            && selected.Parameters[0].RefKind == RefKind.None
            && boundReturnType is not null
            && compilation.ClassifyConversion(selected.ReturnType, boundReturnType).IsImplicit;
        return true;
    }

    private bool TryCreateBindingContext(
        IEnumerable<ITypeSymbol> types,
        out CSharpCompilation compilation,
        out string typeParameters,
        out string constraints,
        out Dictionary<ITypeParameterSymbol, string> substitutions)
    {
        if (_compilation is not CSharpCompilation csharpCompilation)
        {
            compilation = null!;
            typeParameters = string.Empty;
            constraints = string.Empty;
            substitutions = null!;
            return false;
        }

        compilation = csharpCompilation;
        var parameters = new List<ITypeParameterSymbol>();
        foreach (var type in types)
        {
            CollectTypeParameters(type, parameters);
        }

        for (var i = 0; i < parameters.Count; i++)
        {
            foreach (var constraintType in parameters[i].ConstraintTypes)
            {
                CollectTypeParameters(constraintType, parameters);
            }
        }

        substitutions = new(SymbolEqualityComparer.Default);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            var count = 0;
            var name = parameter.Name;
            while (!names.Add(name))
            {
                name = $"{parameter.Name}_{++count}";
            }

            substitutions.Add(parameter, EscapeIdentifier(name));
        }

        var typeParameterSubstitutions = substitutions;
        typeParameters = parameters.Count == 0
            ? string.Empty
            : $"<{string.Join(", ", parameters.Select(parameter => typeParameterSubstitutions[parameter]))}>";
        constraints = string.Join(
            "\n",
            parameters.Select(parameter => GetConstraintClause(parameter, typeParameterSubstitutions)).Where(static clause => clause.Length > 0));
        return true;
    }

    private static void CollectTypeParameters(ITypeSymbol type, List<ITypeParameterSymbol> result)
    {
        switch (type)
        {
            case ITypeParameterSymbol parameter:
                if (!result.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, parameter)))
                {
                    result.Add(parameter);
                }

                break;
            case IArrayTypeSymbol array:
                CollectTypeParameters(array.ElementType, result);
                break;
            case IPointerTypeSymbol pointer:
                CollectTypeParameters(pointer.PointedAtType, result);
                break;
            case IFunctionPointerTypeSymbol functionPointer:
                CollectTypeParameters(functionPointer.Signature.ReturnType, result);
                foreach (var parameter in functionPointer.Signature.Parameters)
                {
                    CollectTypeParameters(parameter.Type, result);
                }

                break;
            case INamedTypeSymbol named:
                if (named.ContainingType is { } containingType)
                {
                    CollectTypeParameters(containingType, result);
                }

                foreach (var argument in named.TypeArguments)
                {
                    CollectTypeParameters(argument, result);
                }

                break;
        }
    }

    private static string GetConstraintClause(
        ITypeParameterSymbol parameter,
        Dictionary<ITypeParameterSymbol, string> substitutions)
    {
        var constraints = new List<string>();
        if (parameter.HasUnmanagedTypeConstraint)
        {
            constraints.Add("unmanaged");
        }
        else if (parameter.HasValueTypeConstraint)
        {
            constraints.Add("struct");
        }
        else if (parameter.HasReferenceTypeConstraint)
        {
            constraints.Add(parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
        }
        else if (parameter.HasNotNullConstraint)
        {
            constraints.Add("notnull");
        }

        constraints.AddRange(parameter.ConstraintTypes.Select(constraint => DisplayFullyQualified(constraint, substitutions)));
        if (parameter.HasConstructorConstraint)
        {
            constraints.Add("new()");
        }

        return constraints.Count == 0
            ? string.Empty
            : $"where {substitutions[parameter]} : {string.Join(", ", constraints)}";
    }

    private SyntaxTree ParseBindingTree(CSharpCompilation compilation, string source)
        => CSharpSyntaxTree.ParseText(
            source,
            _bindingCache.ParseOptions,
            "__OrleansCodeGenSemanticBinding.g.cs");

    private static ResolverDiagnostic CreateInvalidInitializerDiagnostic(
        Mapping mapping,
        INamedTypeSymbol baseType,
        INamedTypeSymbol proxyBaseType,
        ITypeSymbol returnType,
        string methodName)
        => CreateDiagnostic(
            mapping,
            $"Return-value proxy initializer '{Display(baseType)}.{methodName}' must be an accessible, concrete, non-generic instance method selected by overload resolution, with one by-value parameter accepting '{Display(proxyBaseType)}' and a return type assignable to '{Display(returnType)}'.");

    private static string DisplayFullyQualified(
        ITypeSymbol symbol,
        Dictionary<ITypeParameterSymbol, string> substitutions)
    {
        var result = new StringBuilder();
        foreach (var part in symbol.ToDisplayParts(SymbolDisplayFormat.FullyQualifiedFormat))
        {
            if (part.Symbol is ITypeParameterSymbol parameter
                && substitutions.TryGetValue(parameter, out var replacement))
            {
                result.Append(replacement);
            }
            else
            {
                result.Append(part.ToString());
            }
        }

        return result.ToString();
    }

    private static string EscapeIdentifier(string identifier)
        => SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None
            || SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
                ? $"@{identifier}"
                : identifier;

    private static ResolverDiagnostic CreateDiagnostic(Mapping mapping, string message)
        => new(ResolverDiagnosticKind.InvalidMapping, message, mapping.Location);
    private static string Display(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
    private static string DisplayWithAssembly(INamedTypeSymbol type)
        => $"{Display(type)} [{type.ContainingAssembly.Identity}]";
    private static Location? GetLocation(AttributeData attribute) => attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
    private static string GetOrigin(AttributeData attribute, IAssemblySymbol? assembly)
        => $"{assembly?.Identity}|{attribute.ApplicationSyntaxReference?.SyntaxTree.FilePath}|{attribute.ApplicationSyntaxReference?.Span.Start ?? -1}";

    private static ImmutableArray<Mapping> Sort(ImmutableArray<Mapping>.Builder builder)
    {
        builder.Sort(MappingComparer.Instance);
        return builder.ToImmutable();
    }

    private readonly record struct InitializerBindingKey(
        INamedTypeSymbol ProxyBaseType,
        INamedTypeSymbol ProxyInterface,
        ITypeSymbol ReturnType,
        INamedTypeSymbol BaseType,
        string MethodName);

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

    private enum MappingMatchKind
    {
        Exact,
        OpenGeneric,
    }

    private enum CandidateResolutionStatus
    {
        NoMatch,
        Success,
        Failure,
    }

    private sealed class BindingCache
    {
        public BindingCache(Compilation compilation)
        {
            InvokableBaseTypeAttribute = compilation.GetTypeByMetadataName(InvokableBaseTypeAttributeMetadataName);
            DefaultInvokableBaseTypeAttribute = compilation.GetTypeByMetadataName(DefaultInvokableBaseTypeAttributeMetadataName);
            GeneratedActivatorConstructorAttribute = compilation.GetTypeByMetadataName(GeneratedActivatorConstructorAttributeMetadataName);
            ReturnValueProxyAttribute = compilation.GetTypeByMetadataName(ReturnValueProxyAttributeMetadataName);
            ParseOptions = compilation.SyntaxTrees
                .Select(static tree => tree.Options)
                .OfType<CSharpParseOptions>()
                .FirstOrDefault();
        }

        public ConcurrentDictionary<INamedTypeSymbol, Lazy<bool>> ConstructorBindings { get; } =
            new(SymbolEqualityComparer.Default);

        public ConcurrentDictionary<InitializerBindingKey, Lazy<bool>> InitializerBindings { get; } =
            new(InitializerBindingKeyComparer.Instance);

        public ConcurrentDictionary<IMethodSymbol, Lazy<ImmutableArray<Mapping>>> MethodMappings { get; } =
            new(SymbolEqualityComparer.Default);

        public ConcurrentDictionary<INamedTypeSymbol, Lazy<ImmutableArray<Mapping>>> ReturnTypeMappings { get; } =
            new(SymbolEqualityComparer.Default);

        public ConcurrentDictionary<INamedTypeSymbol, Lazy<ImmutableArray<Mapping>>> DefaultMappings { get; } =
            new(SymbolEqualityComparer.Default);

        public ConcurrentDictionary<INamedTypeSymbol, Lazy<ImmutableArray<ResolvedMapping>>> ProxyMappings { get; } =
            new(SymbolEqualityComparer.Default);

        public INamedTypeSymbol? InvokableBaseTypeAttribute { get; }
        public INamedTypeSymbol? DefaultInvokableBaseTypeAttribute { get; }
        public INamedTypeSymbol? GeneratedActivatorConstructorAttribute { get; }
        public INamedTypeSymbol? ReturnValueProxyAttribute { get; }
        public CSharpParseOptions? ParseOptions { get; }
    }

    private sealed class InitializerBindingKeyComparer : IEqualityComparer<InitializerBindingKey>
    {
        public static InitializerBindingKeyComparer Instance { get; } = new();

        public bool Equals(InitializerBindingKey x, InitializerBindingKey y)
            => SymbolEqualityComparer.Default.Equals(x.ProxyBaseType, y.ProxyBaseType)
                && SymbolEqualityComparer.Default.Equals(x.ProxyInterface, y.ProxyInterface)
                && SymbolEqualityComparer.Default.Equals(x.ReturnType, y.ReturnType)
                && SymbolEqualityComparer.Default.Equals(x.BaseType, y.BaseType)
                && string.Equals(x.MethodName, y.MethodName, StringComparison.Ordinal);

        public int GetHashCode(InitializerBindingKey obj)
        {
            unchecked
            {
                var hash = SymbolEqualityComparer.Default.GetHashCode(obj.ProxyBaseType);
                hash = (hash * 397) ^ SymbolEqualityComparer.Default.GetHashCode(obj.ProxyInterface);
                hash = (hash * 397) ^ SymbolEqualityComparer.Default.GetHashCode(obj.ReturnType);
                hash = (hash * 397) ^ SymbolEqualityComparer.Default.GetHashCode(obj.BaseType);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(obj.MethodName);
                return hash;
            }
        }
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

internal enum ResolverDiagnosticKind
{
    MissingMapping,
    InvalidMapping,
}

internal sealed record ResolverDiagnostic(ResolverDiagnosticKind Kind, string Message, Location? Location);

internal readonly record struct ResolvedMapping(
    INamedTypeSymbol ReturnType,
    INamedTypeSymbol InvokableBaseType,
    string ReturnTypeName,
    string InvokableBaseTypeName);
