using System;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// Describes the proxy output for a single interface, including the invokable metadata names owned by that file.
    /// </summary>
    internal sealed class ProxyOutputModel : IEquatable<ProxyOutputModel>
    {
        public ProxyOutputModel(
            ProxyInterfaceModel proxyInterface,
            EquatableArray<EquatableString> ownedInvokableMetadataNames,
            bool useDeclaredInvokableFallback)
        {
            ProxyInterface = proxyInterface;
            OwnedInvokableMetadataNames = ownedInvokableMetadataNames;
            UseDeclaredInvokableFallback = useDeclaredInvokableFallback;
        }

        public ProxyInterfaceModel ProxyInterface { get; }
        public EquatableArray<EquatableString> OwnedInvokableMetadataNames { get; }
        public bool UseDeclaredInvokableFallback { get; }

        public bool Equals(ProxyOutputModel other)
        {
            if (other is null)
            {
                return false;
            }

            return ProxyInterface.Equals(other.ProxyInterface)
                && OwnedInvokableMetadataNames.Equals(other.OwnedInvokableMetadataNames)
                && UseDeclaredInvokableFallback == other.UseDeclaredInvokableFallback;
        }

        public override bool Equals(object obj) => obj is ProxyOutputModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((ProxyInterface.GetHashCode() * 31) + OwnedInvokableMetadataNames.GetHashCode()) * 31
                    + (UseDeclaredInvokableFallback ? 1 : 0);
            }
        }
    }
}
