using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes the proxy output for a single interface, including the invokable metadata names owned by that file.
/// </summary>
internal sealed class ProxyOutputModel(
    ProxyInterfaceModel proxyInterface,
    ImmutableArray<string> ownedInvokableMetadataNames,
    ImmutableArray<string> ownedInvokableActivatorMetadataNames,
    bool useDeclaredInvokableFallback) : IEquatable<ProxyOutputModel>
{
    public ProxyOutputModel(
        ProxyInterfaceModel proxyInterface,
        ImmutableArray<string> ownedInvokableMetadataNames,
        bool useDeclaredInvokableFallback)
        : this(
            proxyInterface,
            ownedInvokableMetadataNames,
            [],
            useDeclaredInvokableFallback)
    {
    }

    public ProxyInterfaceModel ProxyInterface { get; } = proxyInterface;
    public ImmutableArray<string> OwnedInvokableMetadataNames { get; } = StructuralEquality.Normalize(ownedInvokableMetadataNames);
    public ImmutableArray<string> OwnedInvokableActivatorMetadataNames { get; } = StructuralEquality.Normalize(ownedInvokableActivatorMetadataNames);
    public bool UseDeclaredInvokableFallback { get; } = useDeclaredInvokableFallback;

    public bool Equals(ProxyOutputModel other)
    {
        if (other is null)
        {
            return false;
        }

        return ProxyInterface.Equals(other.ProxyInterface)
            && StructuralEquality.SequenceEqual(OwnedInvokableMetadataNames, other.OwnedInvokableMetadataNames)
            && StructuralEquality.SequenceEqual(OwnedInvokableActivatorMetadataNames, other.OwnedInvokableActivatorMetadataNames)
            && UseDeclaredInvokableFallback == other.UseDeclaredInvokableFallback;
    }

    public override bool Equals(object obj) => obj is ProxyOutputModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = ProxyInterface.GetHashCode();
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(OwnedInvokableMetadataNames);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(OwnedInvokableActivatorMetadataNames);
            hash = hash * 31 + (UseDeclaredInvokableFallback ? 1 : 0);
            return hash;
        }
    }
}
