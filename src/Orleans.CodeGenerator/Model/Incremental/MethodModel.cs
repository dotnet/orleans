using System;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// Describes a method parameter for invokable/proxy generation.
    /// </summary>
    internal readonly struct MethodParameterModel : IEquatable<MethodParameterModel>
    {
        public MethodParameterModel(string name, TypeRef type, int ordinal, bool isCancellationToken)
        {
            Name = name;
            Type = type;
            Ordinal = ordinal;
            IsCancellationToken = isCancellationToken;
        }

        public string Name { get; }
        public TypeRef Type { get; }
        public int Ordinal { get; }
        public bool IsCancellationToken { get; }

        public bool Equals(MethodParameterModel other) =>
            string.Equals(Name, other.Name, StringComparison.Ordinal)
            && Type.Equals(other.Type)
            && Ordinal == other.Ordinal
            && IsCancellationToken == other.IsCancellationToken;

        public override bool Equals(object obj) => obj is MethodParameterModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);
                hash = hash * 31 + Type.GetHashCode();
                hash = hash * 31 + Ordinal;
                hash = hash * 31 + (IsCancellationToken ? 1 : 0);
                return hash;
            }
        }

        public static bool operator ==(MethodParameterModel left, MethodParameterModel right) => left.Equals(right);
        public static bool operator !=(MethodParameterModel left, MethodParameterModel right) => !left.Equals(right);
    }

    /// <summary>
    /// Describes a method on a proxy interface for invokable generation.
    /// </summary>
    internal sealed class MethodModel : IEquatable<MethodModel>
    {
        public MethodModel(
            string name,
            TypeRef returnType,
            ImmutableArray<MethodParameterModel> parameters,
            ImmutableArray<TypeParameterModel> typeParameters,
            TypeRef containingInterfaceType,
            TypeRef originalContainingInterfaceType,
            string containingInterfaceName,
            string containingInterfaceGeneratedNamespace,
            int containingInterfaceTypeParameterCount,
            string generatedMethodId,
            string methodId,
            long? responseTimeoutTicks,
            ImmutableArray<CustomInitializerModel> customInitializerMethods,
            bool isCancellable)
        {
            Name = name;
            ReturnType = returnType;
            Parameters = ImmutableArrayValueComparer.Normalize(parameters);
            TypeParameters = ImmutableArrayValueComparer.Normalize(typeParameters);
            ContainingInterfaceType = containingInterfaceType;
            OriginalContainingInterfaceType = originalContainingInterfaceType;
            ContainingInterfaceName = containingInterfaceName;
            ContainingInterfaceGeneratedNamespace = containingInterfaceGeneratedNamespace;
            ContainingInterfaceTypeParameterCount = containingInterfaceTypeParameterCount;
            GeneratedMethodId = generatedMethodId;
            MethodId = methodId;
            ResponseTimeoutTicks = responseTimeoutTicks;
            CustomInitializerMethods = ImmutableArrayValueComparer.Normalize(customInitializerMethods);
            IsCancellable = isCancellable;
        }

        public string Name { get; }
        public TypeRef ReturnType { get; }
        public ImmutableArray<MethodParameterModel> Parameters { get; }
        public ImmutableArray<TypeParameterModel> TypeParameters { get; }
        public TypeRef ContainingInterfaceType { get; }
        public TypeRef OriginalContainingInterfaceType { get; }
        public string ContainingInterfaceName { get; }
        public string ContainingInterfaceGeneratedNamespace { get; }
        public int ContainingInterfaceTypeParameterCount { get; }
        public string GeneratedMethodId { get; }
        public string MethodId { get; }
        public bool HasAlias => !string.Equals(MethodId, GeneratedMethodId, StringComparison.Ordinal);
        public long? ResponseTimeoutTicks { get; }
        public ImmutableArray<CustomInitializerModel> CustomInitializerMethods { get; }
        public bool IsCancellable { get; }

        public bool Equals(MethodModel other)
        {
            if (other is null)
            {
                return false;
            }

            return string.Equals(Name, other.Name, StringComparison.Ordinal)
                && ReturnType.Equals(other.ReturnType)
                && ImmutableArrayValueComparer.Equals(Parameters, other.Parameters)
                && ImmutableArrayValueComparer.Equals(TypeParameters, other.TypeParameters)
                && ContainingInterfaceType.Equals(other.ContainingInterfaceType)
                && OriginalContainingInterfaceType.Equals(other.OriginalContainingInterfaceType)
                && string.Equals(ContainingInterfaceName, other.ContainingInterfaceName, StringComparison.Ordinal)
                && string.Equals(ContainingInterfaceGeneratedNamespace, other.ContainingInterfaceGeneratedNamespace, StringComparison.Ordinal)
                && ContainingInterfaceTypeParameterCount == other.ContainingInterfaceTypeParameterCount
                && string.Equals(GeneratedMethodId, other.GeneratedMethodId, StringComparison.Ordinal)
                && string.Equals(MethodId, other.MethodId, StringComparison.Ordinal)
                && ResponseTimeoutTicks == other.ResponseTimeoutTicks
                && ImmutableArrayValueComparer.Equals(CustomInitializerMethods, other.CustomInitializerMethods)
                && IsCancellable == other.IsCancellable;
        }

        public override bool Equals(object obj) => obj is MethodModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);
                hash = hash * 31 + ReturnType.GetHashCode();
                hash = hash * 31 + ContainingInterfaceType.GetHashCode();
                hash = hash * 31 + OriginalContainingInterfaceType.GetHashCode();
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(ContainingInterfaceName ?? string.Empty);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(ContainingInterfaceGeneratedNamespace ?? string.Empty);
                hash = hash * 31 + ContainingInterfaceTypeParameterCount;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(GeneratedMethodId ?? string.Empty);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(MethodId ?? string.Empty);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(Parameters);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(TypeParameters);
                hash = hash * 31 + ResponseTimeoutTicks.GetHashCode();
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(CustomInitializerMethods);
                hash = hash * 31 + (IsCancellable ? 1 : 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// Describes a custom initializer method associated with an invokable method's attribute.
    /// </summary>
    internal readonly struct CustomInitializerModel : IEquatable<CustomInitializerModel>
    {
        public CustomInitializerModel(string methodName, string argumentValue)
        {
            MethodName = methodName;
            ArgumentValue = argumentValue;
        }

        public string MethodName { get; }
        public string ArgumentValue { get; }

        public bool Equals(CustomInitializerModel other) =>
            string.Equals(MethodName, other.MethodName, StringComparison.Ordinal)
            && string.Equals(ArgumentValue, other.ArgumentValue, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is CustomInitializerModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return StringComparer.Ordinal.GetHashCode(MethodName ?? string.Empty) * 31
                    + StringComparer.Ordinal.GetHashCode(ArgumentValue ?? string.Empty);
            }
        }

        public static bool operator ==(CustomInitializerModel left, CustomInitializerModel right) => left.Equals(right);
        public static bool operator !=(CustomInitializerModel left, CustomInitializerModel right) => !left.Equals(right);
    }
}
