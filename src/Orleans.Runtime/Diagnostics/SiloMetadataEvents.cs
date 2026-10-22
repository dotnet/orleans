using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Diagnostics;

internal static class SiloMetadataEvents
{
    internal const string ListenerName = "Orleans.SiloMetadata";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal sealed class CacheUpdated(
        SiloAddress observerSiloAddress,
        long sequence,
        MembershipVersion membershipVersion,
        ImmutableArray<SiloAddress> cachedSilos)
    {
        public readonly SiloAddress ObserverSiloAddress = observerSiloAddress;
        public readonly long Sequence = sequence;
        public readonly MembershipVersion MembershipVersion = membershipVersion;
        public readonly ImmutableArray<SiloAddress> CachedSilos = cachedSilos;
    }

    internal static bool IsCacheUpdatedEnabled => Listener.IsEnabled(nameof(CacheUpdated));

    internal static void EmitCacheUpdated(
        SiloAddress observerSiloAddress,
        long sequence,
        MembershipVersion membershipVersion,
        IEnumerable<SiloAddress> cachedSilos)
    {
        if (!Listener.IsEnabled(nameof(CacheUpdated)))
        {
            return;
        }

        Emit(observerSiloAddress, sequence, membershipVersion, cachedSilos.ToImmutableArray());

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            SiloAddress observerSiloAddress,
            long sequence,
            MembershipVersion membershipVersion,
            ImmutableArray<SiloAddress> cachedSilos)
        {
            Listener.Write(nameof(CacheUpdated), new CacheUpdated(observerSiloAddress, sequence, membershipVersion, cachedSilos));
        }
    }
}
