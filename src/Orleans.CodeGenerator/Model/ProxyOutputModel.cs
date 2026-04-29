namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes the proxy output for a single interface, including the invokable metadata names owned by that file.
/// </summary>
internal sealed record class ProxyOutputModel(
    ProxyInterfaceModel ProxyInterface,
    EquatableArray<string> OwnedInvokableMetadataNames,
    EquatableArray<string> OwnedInvokableActivatorMetadataNames,
    bool UseDeclaredInvokableFallback)
{
    public ProxyOutputModel(
        ProxyInterfaceModel proxyInterface,
        EquatableArray<string> ownedInvokableMetadataNames,
        bool useDeclaredInvokableFallback)
        : this(
            proxyInterface,
            ownedInvokableMetadataNames,
            EquatableArray<string>.Empty,
            useDeclaredInvokableFallback)
    {
    }
}
