using System.Diagnostics;
using Orleans.Diagnostics;

namespace Orleans.Runtime.MembershipService;

internal static class MembershipTableManagerDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansMembershipDiagnostics.ListenerName);

    internal static void EmitMembershipDiagnostics(MembershipTableSnapshot previousSnapshot, MembershipTableSnapshot newSnapshot, SiloAddress observerAddress)
    {
        if (!Listener.IsEnabled())
        {
            return;
        }

        Emit(Listener, previousSnapshot, newSnapshot, observerAddress);

        static void Emit(DiagnosticListener listener, MembershipTableSnapshot previousSnapshot, MembershipTableSnapshot newSnapshot, SiloAddress observerAddress)
        {
            if (listener.IsEnabled(OrleansMembershipDiagnostics.EventNames.ViewChanged))
            {
                listener.Write(OrleansMembershipDiagnostics.EventNames.ViewChanged, new MembershipViewChangedEvent(
                    newSnapshot,
                    observerAddress));
            }

            if (!listener.IsEnabled(OrleansMembershipDiagnostics.EventNames.SiloStatusChanged))
            {
                return;
            }

            foreach (var (siloAddress, newEntry) in newSnapshot.Entries)
            {
                if (previousSnapshot.Entries.TryGetValue(siloAddress, out var oldEntry))
                {
                    if (oldEntry.Status == newEntry.Status)
                    {
                        continue;
                    }

                    listener.Write(OrleansMembershipDiagnostics.EventNames.SiloStatusChanged, new SiloStatusChangedEvent(
                        oldEntry,
                        newEntry,
                        observerAddress));

                    continue;
                }

                listener.Write(OrleansMembershipDiagnostics.EventNames.SiloStatusChanged, new SiloStatusChangedEvent(
                    oldEntry: null,
                    newEntry,
                    observerAddress));
            }
        }
    }
}
