namespace Orleans.CodeGenerator.Model;

/// <summary>
/// The spelled declaration of a generated proxy class. Every constructor shares <see cref="ConstructorStatements"/>.
/// </summary>
/// <remarks>
/// Grain reference proxies declare a nested <see cref="Activator"/>, which the runtime creates once for each grain type and
/// interface, and which creates every reference for them. Proxies with instance copiers also declare a nested type named
/// <see cref="SharedTypeName"/>, derived from <c>GrainReferenceShared</c>, which holds <see cref="SharedFields"/>, resolved
/// by <see cref="SharedStatements"/>. The activator constructs one instance of it and passes it to every proxy it creates.
/// Other proxies hold their copiers in <see cref="Fields"/>.
/// </remarks>
internal sealed record ProxyEmissionPlan(
    string ClassName,
    string BaseType,
    string InterfaceType,
    EquatableArray<GenericParameterEmissionPlan> TypeParameters,
    EquatableArray<string> Fields,
    EquatableArray<string> Constructors,
    EquatableArray<string> ConstructorStatements,
    EquatableArray<ProxyMethodEmissionPlan> Methods,
    ProxyActivatorEmissionPlan? Activator,
    string? SharedTypeName,
    EquatableArray<string> SharedFields,
    EquatableArray<string> SharedStatements)
{
    /// <summary>The metadata name of the generated type within <paramref name="generatedNamespace"/>.</summary>
    public string GetMetadataName(string generatedNamespace)
    {
        var name = string.IsNullOrEmpty(generatedNamespace) ? ClassName : $"{generatedNamespace}.{ClassName}";
        return TypeParameters.Count == 0 ? name : $"{name}`{TypeParameters.Count}";
    }
}

/// <summary>
/// The nested activator of a grain reference proxy: the attribute which names it on the proxy, its type name, the type
/// and initializer of the shared state it holds, and the proxy type it creates.
/// </summary>
internal sealed record ProxyActivatorEmissionPlan(
    string Attribute,
    string TypeName,
    string SharedType,
    string SharedInitializer,
    string ProxyType);

/// <summary>An explicit interface implementation: its declaration line and body statements.</summary>
internal sealed record ProxyMethodEmissionPlan(string Declaration, EquatableArray<string> Statements);
