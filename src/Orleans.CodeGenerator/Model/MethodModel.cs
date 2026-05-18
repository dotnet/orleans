namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes a method parameter for invokable/proxy generation.
/// </summary>
internal readonly record struct MethodParameterModel(string Name, TypeRef Type, int Ordinal, bool IsCancellationToken);

/// <summary>
/// Describes a method on a proxy interface for invokable generation.
/// </summary>
internal sealed record class MethodModel(
    string Name,
    TypeRef ReturnType,
    EquatableArray<MethodParameterModel> Parameters,
    EquatableArray<TypeParameterModel> TypeParameters,
    TypeRef ContainingInterfaceType,
    TypeRef OriginalContainingInterfaceType,
    string ContainingInterfaceName,
    string ContainingInterfaceGeneratedNamespace,
    int ContainingInterfaceTypeParameterCount,
    string GeneratedMethodId,
    string MethodId,
    long? ResponseTimeoutTicks,
    EquatableArray<CustomInitializerModel> CustomInitializerMethods,
    bool IsCancellable)
{
    public bool HasAlias => !string.Equals(MethodId, GeneratedMethodId, StringComparison.Ordinal);
}

/// <summary>
/// Describes a custom initializer method associated with an invokable method's attribute.
/// </summary>
internal readonly record struct CustomInitializerModel(string MethodName, string ArgumentValue);
