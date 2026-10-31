namespace Orleans.CodeGenerator.Model;

/// <summary>
/// The spelled declaration of a generated invokable, planned while its proxy discovers the invokable.
/// </summary>
/// <remarks>
/// A cancellable invokable has both cancellation fields. <see cref="Arguments"/> lists every method parameter, including cancellation tokens.
/// </remarks>
internal sealed record InvokableEmissionPlan(
    string ClassName,
    bool IsPublic,
    string BaseType,
    EquatableArray<string> CompoundTypeAliases,
    EquatableArray<GenericParameterEmissionPlan> TypeParameters,
    EquatableArray<string> Fields,
    InvokableConstructorEmissionPlan? Constructor,
    string? ResponseTimeoutTicks,
    string TimeSpanType,
    int ArgumentCount,
    string MethodName,
    string InterfaceName,
    string ActivityName,
    string SystemType,
    string InterfaceType,
    string MethodInfoType,
    string TargetHolderType,
    string TargetType,
    bool IsExtension,
    string? CancellationTokenSourceField,
    string? CancellationTokenField,
    string CancellationTokenType,
    EquatableArray<string> DisposeStatements,
    bool DisposeBase,
    EquatableArray<InvokableArgumentEmissionPlan> Arguments,
    string MaxArgumentIndex,
    string ReturnType,
    string InvokeTarget,
    string InvokeArguments,
    bool OptOutOfGrainCancellationTokenScan)
{
    /// <summary>The metadata name of the generated type within <paramref name="generatedNamespace"/>.</summary>
    public string GetMetadataName(string generatedNamespace)
    {
        var name = string.IsNullOrEmpty(generatedNamespace) ? ClassName : $"{generatedNamespace}.{ClassName}";
        return TypeParameters.Count == 0 ? name : $"{name}`{TypeParameters.Count}";
    }
}

internal sealed record InvokableConstructorEmissionPlan(string Parameters, string BaseArguments, EquatableArray<string> Statements);

internal sealed record InvokableArgumentEmissionPlan(string Ordinal, string FieldName, string Type);
