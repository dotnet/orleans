using System.Diagnostics;
using System.Linq;
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
                var activeSiloCount = newSnapshot.Entries.Count(e => e.Value.Status == SiloStatus.Active);
                listener.Write(OrleansMembershipDiagnostics.EventNames.ViewChanged, new MembershipViewChangedEvent(
                    newSnapshot.Version,
                    activeSiloCount,
                    newSnapshot.Entries.Count,
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
                        siloAddress,
                        oldEntry.Status.ToString(),
                        newEntry.Status.ToString(),
                        observerAddress));

                    EmitDerivedStatusEvent(listener, siloAddress, newEntry.Status, observerAddress);
                    continue;
                }

                listener.Write(OrleansMembershipDiagnostics.EventNames.SiloStatusChanged, new SiloStatusChangedEvent(
                    siloAddress,
                    "None",
                    newEntry.Status.ToString(),
                    observerAddress));

                EmitDerivedStatusEvent(listener, siloAddress, newEntry.Status, observerAddress);
            }
        }
    }

    private static void EmitDerivedStatusEvent(DiagnosticListener listener, SiloAddress siloAddress, SiloStatus newStatus, SiloAddress observerAddress)
    {
        switch (newStatus)
        {
            case SiloStatus.Active:
                EmitSiloBecameActive(listener, siloAddress, observerAddress);
                break;
            case SiloStatus.Joining:
                EmitSiloJoining(listener, siloAddress, observerAddress);
                break;
            case SiloStatus.Dead:
                EmitSiloDeclaredDead(listener, siloAddress, observerAddress);
                break;
        }
    }

    private static void EmitSiloBecameActive(DiagnosticListener listener, SiloAddress siloAddress, SiloAddress observerAddress)
    {
        if (!listener.IsEnabled(OrleansMembershipDiagnostics.EventNames.SiloBecameActive))
        {
            return;
        }

        Emit(listener, siloAddress, observerAddress);

        static void Emit(DiagnosticListener listener, SiloAddress siloAddress, SiloAddress observerAddress)
        {
            listener.Write(OrleansMembershipDiagnostics.EventNames.SiloBecameActive, new SiloBecameActiveEvent(
                siloAddress,
                observerAddress));
        }
    }

    private static void EmitSiloJoining(DiagnosticListener listener, SiloAddress siloAddress, SiloAddress observerAddress)
    {
        if (!listener.IsEnabled(OrleansMembershipDiagnostics.EventNames.SiloJoining))
        {
            return;
        }

        Emit(listener, siloAddress, observerAddress);

        static void Emit(DiagnosticListener listener, SiloAddress siloAddress, SiloAddress observerAddress)
        {
            listener.Write(OrleansMembershipDiagnostics.EventNames.SiloJoining, new SiloJoiningEvent(
                siloAddress,
                observerAddress));
        }
    }

    private static void EmitSiloDeclaredDead(DiagnosticListener listener, SiloAddress siloAddress, SiloAddress observerAddress)
    {
        if (!listener.IsEnabled(OrleansMembershipDiagnostics.EventNames.SiloDeclaredDead))
        {
            return;
        }

        Emit(listener, siloAddress, observerAddress);

        static void Emit(DiagnosticListener listener, SiloAddress siloAddress, SiloAddress observerAddress)
        {
            listener.Write(OrleansMembershipDiagnostics.EventNames.SiloDeclaredDead, new SiloDeclaredDeadEvent(
                siloAddress,
                "MembershipTableUpdate",
                observerAddress));
        }
    }
}
