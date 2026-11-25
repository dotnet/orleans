namespace Orleans.CodeGenerator.Model;

/// <summary>
/// The spelled declaration of a generated proxy class. Every constructor shares <see cref="ConstructorStatements"/>.
/// </summary>
internal sealed record ProxyEmissionPlan(
    string ClassName,
    string BaseType,
    string InterfaceType,
    EquatableArray<GenericParameterEmissionPlan> TypeParameters,
    EquatableArray<string> Fields,
    EquatableArray<string> Constructors,
    EquatableArray<string> ConstructorStatements,
    EquatableArray<ProxyMethodEmissionPlan> Methods)
{
    /// <summary>The metadata name of the generated type within <paramref name="generatedNamespace"/>.</summary>
    public string GetMetadataName(string generatedNamespace)
    {
        var name = string.IsNullOrEmpty(generatedNamespace) ? ClassName : $"{generatedNamespace}.{ClassName}";
        return TypeParameters.Count == 0 ? name : $"{name}`{TypeParameters.Count}";
    }
}

/// <summary>An explicit interface implementation: its declaration line and body statements.</summary>
internal sealed record ProxyMethodEmissionPlan(string Declaration, EquatableArray<string> Statements);
