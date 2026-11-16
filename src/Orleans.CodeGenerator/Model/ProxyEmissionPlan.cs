namespace Orleans.CodeGenerator.Model;

/// <summary>
/// The spelled declaration of a generated proxy class. Every constructor shares <see cref="ConstructorStatements"/>.
/// </summary>
/// <remarks>
/// Grain reference proxies have an <see cref="Activator"/>, a sibling type which the runtime creates once for each grain
/// type and interface, and which creates every reference for them. Proxies with instance copiers also have a sibling type
/// derived from <c>GrainReferenceShared</c> which holds <see cref="SharedFields"/>, resolved by
/// <see cref="SharedStatements"/>. The activator constructs one instance of it and passes it to every proxy it creates.
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
/// The activator of a grain reference proxy: its class name, the type and initializer of the shared state it holds, the
/// proxy type it creates, and the class name of the proxy's shared state type when the proxy has copiers.
/// </summary>
internal sealed record ProxyActivatorEmissionPlan(
    string ClassName,
    string SharedType,
    string SharedInitializer,
    string ProxyType,
    string? SharedClassName);

/// <summary>An explicit interface implementation: its declaration line and body statements.</summary>
internal sealed record ProxyMethodEmissionPlan(string Declaration, EquatableArray<string> Statements);
