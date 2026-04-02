using System.Diagnostics;
using System.Linq;
using Orleans.Diagnostics;

namespace Orleans.Runtime.MembershipService
{
    internal partial class MembershipTableManager
    {
        private static readonly DiagnosticListener _diagnosticListener = new(OrleansMembershipDiagnostics.ListenerName);

        private void EmitMembershipDiagnostics(MembershipTableSnapshot previousSnapshot, MembershipTableSnapshot newSnapshot)
        {
            if (!_diagnosticListener.IsEnabled())
            {
                return;
            }

            var observerAddress = this.myAddress;

            if (_diagnosticListener.IsEnabled(OrleansMembershipDiagnostics.EventNames.ViewChanged))
            {
                var activeSiloCount = newSnapshot.Entries.Count(e => e.Value.Status == SiloStatus.Active);
                _diagnosticListener.Write(OrleansMembershipDiagnostics.EventNames.ViewChanged, new MembershipViewChangedEvent(
                    newSnapshot.Version,
                    activeSiloCount,
                    newSnapshot.Entries.Count,
                    observerAddress));
            }

            if (!_diagnosticListener.IsEnabled(OrleansMembershipDiagnostics.EventNames.SiloStatusChanged))
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

                    _diagnosticListener.Write(OrleansMembershipDiagnostics.EventNames.SiloStatusChanged, new SiloStatusChangedEvent(
                        siloAddress,
                        oldEntry.Status.ToString(),
                        newEntry.Status.ToString(),
                        observerAddress));

                    EmitDerivedStatusEvent(siloAddress, newEntry.Status, observerAddress);
                    continue;
                }

                _diagnosticListener.Write(OrleansMembershipDiagnostics.EventNames.SiloStatusChanged, new SiloStatusChangedEvent(
                    siloAddress,
                    "None",
                    newEntry.Status.ToString(),
                    observerAddress));

                EmitDerivedStatusEvent(siloAddress, newEntry.Status, observerAddress);
            }
        }

        private void EmitDerivedStatusEvent(SiloAddress siloAddress, SiloStatus newStatus, SiloAddress observerAddress)
        {
            if (newStatus == SiloStatus.Active && _diagnosticListener.IsEnabled(OrleansMembershipDiagnostics.EventNames.SiloBecameActive))
            {
                _diagnosticListener.Write(OrleansMembershipDiagnostics.EventNames.SiloBecameActive, new SiloBecameActiveEvent(siloAddress, observerAddress));
            }
            else if (newStatus == SiloStatus.Joining && _diagnosticListener.IsEnabled(OrleansMembershipDiagnostics.EventNames.SiloJoining))
            {
                _diagnosticListener.Write(OrleansMembershipDiagnostics.EventNames.SiloJoining, new SiloJoiningEvent(siloAddress, observerAddress));
            }
            else if (newStatus == SiloStatus.Dead && _diagnosticListener.IsEnabled(OrleansMembershipDiagnostics.EventNames.SiloDeclaredDead))
            {
                _diagnosticListener.Write(OrleansMembershipDiagnostics.EventNames.SiloDeclaredDead, new SiloDeclaredDeadEvent(
                    siloAddress,
                    "MembershipTableUpdate",
                    observerAddress));
            }
        }
    }
}
