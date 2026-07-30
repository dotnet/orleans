using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator;

/// <summary>
/// Describes the proxy base for an invokable method, including whether the proxy is a grain reference or extension, and what invokable base types should be used for a given return type.
/// </summary>
internal sealed class InvokableMethodProxyBase(ProxyGenerationContext generationContext, InvokableMethodProxyBaseId descriptor, Dictionary<INamedTypeSymbol, INamedTypeSymbol> invokableBaseTypes) : IEquatable<InvokableMethodProxyBase>
{

    /// <summary>
    /// Gets the proxy generation context.
    /// </summary>
    public ProxyGenerationContext GenerationContext { get; } = generationContext;

    /// <summary>
    /// Gets the proxy base id.
    /// </summary>
    public InvokableMethodProxyBaseId Key { get; } = descriptor;

    /// <summary>
    /// Gets the proxy base type, eg <c>GrainReference</c>.
    /// </summary>
    public INamedTypeSymbol ProxyBaseType => Key.ProxyBaseType;

    /// <summary>
    /// Gets a value indicating whether this descriptor represents an extension.
    /// </summary>
    public bool IsExtension => Key.IsExtension;

    /// <summary>
    /// Gets the components of the compound type alias used to refer to this proxy base.
    /// </summary>
    public ImmutableArray<CompoundTypeAliasComponent> CompositeAliasComponents => Key.CompositeAliasComponents;

    /// <summary>
    /// Gets the dictionary of invokable base types. This indicates what invokable base type (eg, ValueTaskRequest) should be used for a given return type (eg, ValueTask).
    /// </summary>
    public IReadOnlyDictionary<INamedTypeSymbol, INamedTypeSymbol> InvokableBaseTypes { get; } = invokableBaseTypes ?? throw new ArgumentNullException(nameof(invokableBaseTypes));

    public bool Equals(InvokableMethodProxyBase other) => other is not null && Key.Equals(other.Key);
    public override bool Equals(object obj) => obj is InvokableMethodProxyBase other && Equals(other);
    public override int GetHashCode() => Key.GetHashCode();
    public override string ToString() => Key.ToString();
}
