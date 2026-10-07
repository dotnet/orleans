using System;
using System.Collections.Generic;
using System.Reflection;
using Orleans.Serialization.Configuration;

namespace Orleans.Serialization.Serializers;

internal enum GenericConstraintValidationResult
{
    Valid,
    Invalid,
    Unknown
}

internal static class GenericConstraintValidator
{
    // This flag was added to GenericParameterAttributes after .NET 8.
    private const GenericParameterAttributes AllowByRefLike = (GenericParameterAttributes)0x20;

    internal static GenericConstraintValidationResult Validate(
        Type definition,
        Type[] arguments,
        IReadOnlyDictionary<Type, TypeManifestOptions.ClosedTypeMetadata> metadata)
    {
        var parameters = definition.GetGenericArguments();
        if (parameters.Length != arguments.Length)
            throw new ArgumentException($"Generic implementation {definition} requires {parameters.Length} arguments, but {arguments.Length} were supplied.", nameof(arguments));
        var result = GenericConstraintValidationResult.Valid;
        for (var index = 0; index < parameters.Length; index++)
        {
            var argument = arguments[index];
            var attributes = parameters[index].GenericParameterAttributes;
            if (argument.IsPointer || argument.IsByRef || argument == typeof(void)
#if NET5_0_OR_GREATER
                || argument.IsFunctionPointer || argument == typeof(ArgIterator)
#else
                || argument.FullName == "System.ArgIterator" && argument.Assembly == typeof(TypedReference).Assembly
#endif
                || argument == typeof(TypedReference) || argument == typeof(RuntimeArgumentHandle)
                || argument.IsByRefLike && (attributes & AllowByRefLike) == 0)
            {
                return GenericConstraintValidationResult.Invalid;
            }

            if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0 && argument.IsValueType)
                return GenericConstraintValidationResult.Invalid;
            if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0
                && (!argument.IsValueType || Nullable.GetUnderlyingType(argument) is not null))
                return GenericConstraintValidationResult.Invalid;
            if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 && !argument.IsValueType)
            {
#if NET10_0_OR_GREATER
                if (argument.IsAbstract) return GenericConstraintValidationResult.Invalid;
#endif
                if (metadata.TryGetValue(argument, out var argumentMetadata))
                {
                    if (!argumentMetadata.HasPublicParameterlessConstructor) return GenericConstraintValidationResult.Invalid;
                }
                else
                {
                    result = GenericConstraintValidationResult.Unknown;
                }
            }

            foreach (var constraint in parameters[index].GetGenericParameterConstraints())
            {
                var constraintResult = IsAssignableTo(new(argument), new(constraint, arguments), metadata);
                if (constraintResult == GenericConstraintValidationResult.Invalid) return constraintResult;
                if (constraintResult == GenericConstraintValidationResult.Unknown) result = constraintResult;
            }
        }

        return result;
    }

    private static GenericConstraintValidationResult IsAssignableTo(
        TypeExpression source,
        TypeExpression target,
        IReadOnlyDictionary<Type, TypeManifestOptions.ClosedTypeMetadata> metadata)
    {
        source = source.ResolveParameter();
        target = target.ResolveParameter();
        if (source.Type.IsGenericParameter || target.Type.IsGenericParameter) return GenericConstraintValidationResult.Unknown;
        if (AreEquivalent(source, target)) return GenericConstraintValidationResult.Valid;

        if (!source.Type.ContainsGenericParameters && !target.Type.ContainsGenericParameters)
            return target.Type.IsAssignableFrom(source.Type) ? GenericConstraintValidationResult.Valid : GenericConstraintValidationResult.Invalid;

        if (!target.Type.ContainsGenericParameters && !target.Type.IsGenericType && !target.Type.IsArray)
            return target.Type.IsAssignableFrom(source.Type) ? GenericConstraintValidationResult.Valid : GenericConstraintValidationResult.Invalid;

        if (source.Type.IsArray && target.Type.IsArray)
        {
            if (source.Type.GetArrayRank() != target.Type.GetArrayRank() || source.Type.IsSZArray != target.Type.IsSZArray)
                return GenericConstraintValidationResult.Invalid;
            var sourceElement = new TypeExpression(source.Type.GetElementType()!, source.Bindings).ResolveParameter();
            var targetElement = new TypeExpression(target.Type.GetElementType()!, target.Bindings).ResolveParameter();
            if (sourceElement.Type.IsValueType || targetElement.Type.IsValueType) return GenericConstraintValidationResult.Invalid;
            return IsAssignableTo(sourceElement, targetElement, metadata);
        }

        if (source.Type.IsGenericType && target.Type.IsGenericType
            && source.Type.GetGenericTypeDefinition() == target.Type.GetGenericTypeDefinition())
        {
            var sourceArguments = source.Type.GetGenericArguments();
            var targetArguments = target.Type.GetGenericArguments();
            var parameters = target.Type.GetGenericTypeDefinition().GetGenericArguments();
            var result = GenericConstraintValidationResult.Valid;
            for (var index = 0; index < parameters.Length; index++)
            {
                var sourceArgument = new TypeExpression(sourceArguments[index], source.Bindings).ResolveParameter();
                var targetArgument = new TypeExpression(targetArguments[index], target.Bindings).ResolveParameter();
                if (AreEquivalent(sourceArgument, targetArgument)) continue;
                var variance = parameters[index].GenericParameterAttributes & GenericParameterAttributes.VarianceMask;
                if (sourceArgument.Type.IsValueType || targetArgument.Type.IsValueType || variance == GenericParameterAttributes.None)
                    return GenericConstraintValidationResult.Invalid;
                var argumentResult = variance == GenericParameterAttributes.Covariant
                    ? IsAssignableTo(sourceArgument, targetArgument, metadata)
                    : IsAssignableTo(targetArgument, sourceArgument, metadata);
                if (argumentResult == GenericConstraintValidationResult.Invalid) return argumentResult;
                if (argumentResult == GenericConstraintValidationResult.Unknown) result = argumentResult;
            }

            return result;
        }

        if (target.Type.IsInterface)
        {
            if (source.Type == typeof(object)) return GenericConstraintValidationResult.Invalid;
            if (!TryGetMetadata(source, metadata, out var sourceMetadata)) return GenericConstraintValidationResult.Unknown;
            var result = GenericConstraintValidationResult.Invalid;
            foreach (var implemented in sourceMetadata.Interfaces)
            {
                // Captured interfaces include inherited interfaces, so only matching definitions need comparison.
                if (!implemented.IsGenericType || !target.Type.IsGenericType
                    || implemented.GetGenericTypeDefinition() != target.Type.GetGenericTypeDefinition()) continue;
                var interfaceResult = IsAssignableTo(new(implemented), target, metadata);
                if (interfaceResult == GenericConstraintValidationResult.Valid) return interfaceResult;
                if (interfaceResult == GenericConstraintValidationResult.Unknown) result = interfaceResult;
            }

            return result;
        }

        return source.Type.BaseType is { } baseType
            ? IsAssignableTo(new(baseType, source.Bindings), target, metadata)
            : GenericConstraintValidationResult.Invalid;
    }

    private static bool TryGetMetadata(
        TypeExpression expression,
        IReadOnlyDictionary<Type, TypeManifestOptions.ClosedTypeMetadata> metadata,
        out TypeManifestOptions.ClosedTypeMetadata result)
    {
        if (!expression.Type.ContainsGenericParameters) return metadata.TryGetValue(expression.Type, out result!);
        foreach (var entry in metadata)
        {
            if (AreEquivalent(expression, new(entry.Key)))
            {
                result = entry.Value;
                return true;
            }
        }

        result = null!;
        return false;
    }

    private static bool AreEquivalent(TypeExpression left, TypeExpression right)
    {
        left = left.ResolveParameter();
        right = right.ResolveParameter();
        if (!left.Type.ContainsGenericParameters && !right.Type.ContainsGenericParameters) return left.Type == right.Type;
        if (left.Type.IsGenericParameter || right.Type.IsGenericParameter) return false;
        if (left.Type.IsArray || right.Type.IsArray)
            return left.Type.IsArray && right.Type.IsArray
                && left.Type.GetArrayRank() == right.Type.GetArrayRank() && left.Type.IsSZArray == right.Type.IsSZArray
                && AreEquivalent(new(left.Type.GetElementType()!, left.Bindings), new(right.Type.GetElementType()!, right.Bindings));
        if (!left.Type.IsGenericType || !right.Type.IsGenericType
            || left.Type.GetGenericTypeDefinition() != right.Type.GetGenericTypeDefinition()) return left.Type == right.Type;
        var leftArguments = left.Type.GetGenericArguments();
        var rightArguments = right.Type.GetGenericArguments();
        for (var index = 0; index < leftArguments.Length; index++)
        {
            if (!AreEquivalent(new(leftArguments[index], left.Bindings), new(rightArguments[index], right.Bindings))) return false;
        }

        return true;
    }

    private readonly record struct TypeExpression(Type Type, Type[]? Bindings = null)
    {
        internal TypeExpression ResolveParameter() => Type.IsGenericParameter && Bindings is { } bindings
            ? new(bindings[Type.GenericParameterPosition])
            : this;
    }
}
