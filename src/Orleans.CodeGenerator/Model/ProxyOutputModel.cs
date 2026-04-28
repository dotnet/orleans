using System;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model
{
    /// <summary>
    /// Describes the proxy output for a single interface, including the invokable metadata names owned by that file.
    /// </summary>
    internal sealed class ProxyOutputModel : IEquatable<ProxyOutputModel>
    {
        public ProxyOutputModel(
            ProxyInterfaceModel proxyInterface,
            ImmutableArray<string> ownedInvokableMetadataNames,
            bool useDeclaredInvokableFallback)
        {
            ProxyInterface = proxyInterface;
            OwnedInvokableMetadataNames = StructuralEquality.Normalize(ownedInvokableMetadataNames);
            UseDeclaredInvokableFallback = useDeclaredInvokableFallback;
        }

        public ProxyInterfaceModel ProxyInterface { get; }
        public ImmutableArray<string> OwnedInvokableMetadataNames { get; }
        public bool UseDeclaredInvokableFallback { get; }

        public bool Equals(ProxyOutputModel other)
        {
            if (other is null)
            {
                return false;
            }

            return ProxyInterface.Equals(other.ProxyInterface)
                && StructuralEquality.SequenceEqual(OwnedInvokableMetadataNames, other.OwnedInvokableMetadataNames)
                && UseDeclaredInvokableFallback == other.UseDeclaredInvokableFallback;
        }

        public override bool Equals(object obj) => obj is ProxyOutputModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((ProxyInterface.GetHashCode() * 31) + StructuralEquality.GetSequenceHashCode(OwnedInvokableMetadataNames)) * 31
                    + (UseDeclaredInvokableFallback ? 1 : 0);
            }
        }
    }
}
