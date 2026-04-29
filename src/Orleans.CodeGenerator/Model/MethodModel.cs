using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes a method parameter for invokable/proxy generation.
/// </summary>
internal readonly record struct MethodParameterModel(string Name, TypeRef Type, int Ordinal, bool IsCancellationToken);

/// <summary>
/// Describes a method on a proxy interface for invokable generation.
/// </summary>
internal sealed class MethodModel(
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
    bool isCancellable) : IEquatable<MethodModel>
{
    public string Name { get; } = name;
    public TypeRef ReturnType { get; } = returnType;
    public ImmutableArray<MethodParameterModel> Parameters { get; } = StructuralEquality.Normalize(parameters);
    public ImmutableArray<TypeParameterModel> TypeParameters { get; } = StructuralEquality.Normalize(typeParameters);
    public TypeRef ContainingInterfaceType { get; } = containingInterfaceType;
    public TypeRef OriginalContainingInterfaceType { get; } = originalContainingInterfaceType;
    public string ContainingInterfaceName { get; } = containingInterfaceName;
    public string ContainingInterfaceGeneratedNamespace { get; } = containingInterfaceGeneratedNamespace;
    public int ContainingInterfaceTypeParameterCount { get; } = containingInterfaceTypeParameterCount;
    public string GeneratedMethodId { get; } = generatedMethodId;
    public string MethodId { get; } = methodId;
    public bool HasAlias => !string.Equals(MethodId, GeneratedMethodId, StringComparison.Ordinal);
    public long? ResponseTimeoutTicks { get; } = responseTimeoutTicks;
    public ImmutableArray<CustomInitializerModel> CustomInitializerMethods { get; } = StructuralEquality.Normalize(customInitializerMethods);
    public bool IsCancellable { get; } = isCancellable;

    public bool Equals(MethodModel other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(Name, other.Name, StringComparison.Ordinal)
            && ReturnType.Equals(other.ReturnType)
            && StructuralEquality.SequenceEqual(Parameters, other.Parameters)
            && StructuralEquality.SequenceEqual(TypeParameters, other.TypeParameters)
            && ContainingInterfaceType.Equals(other.ContainingInterfaceType)
            && OriginalContainingInterfaceType.Equals(other.OriginalContainingInterfaceType)
            && string.Equals(ContainingInterfaceName, other.ContainingInterfaceName, StringComparison.Ordinal)
            && string.Equals(ContainingInterfaceGeneratedNamespace, other.ContainingInterfaceGeneratedNamespace, StringComparison.Ordinal)
            && ContainingInterfaceTypeParameterCount == other.ContainingInterfaceTypeParameterCount
            && string.Equals(GeneratedMethodId, other.GeneratedMethodId, StringComparison.Ordinal)
            && string.Equals(MethodId, other.MethodId, StringComparison.Ordinal)
            && ResponseTimeoutTicks == other.ResponseTimeoutTicks
            && StructuralEquality.SequenceEqual(CustomInitializerMethods, other.CustomInitializerMethods)
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
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(Parameters);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(TypeParameters);
            hash = hash * 31 + ResponseTimeoutTicks.GetHashCode();
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(CustomInitializerMethods);
            hash = hash * 31 + (IsCancellable ? 1 : 0);
            return hash;
        }
    }
}

/// <summary>
/// Describes a custom initializer method associated with an invokable method's attribute.
/// </summary>
internal readonly record struct CustomInitializerModel(string MethodName, string ArgumentValue);
