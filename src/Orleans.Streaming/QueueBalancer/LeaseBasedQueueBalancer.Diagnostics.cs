using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Orleans.Diagnostics;

#nullable disable
namespace Orleans.Streams;

public partial class LeaseBasedQueueBalancer
{
    private static readonly DiagnosticListener s_diagnosticListener = new(OrleansStreamingDiagnostics.ListenerName);

    private void EmitQueueBalancerChangedDiagnostics()
    {
        if (!s_diagnosticListener.IsEnabled(OrleansStreamingDiagnostics.EventNames.QueueBalancerChanged))
        {
            return;
        }

        s_diagnosticListener.Write(
            OrleansStreamingDiagnostics.EventNames.QueueBalancerChanged,
            new QueueBalancerChangedEvent(
                _name,
                SiloAddress,
                _myQueues.Count,
                _responsibility,
                _activeSiloCount));
    }

    private void EmitQueueChangeDiagnostics(HashSet<QueueId> oldQueues, HashSet<QueueId> newQueues)
    {
        if (!s_diagnosticListener.IsEnabled())
        {
            return;
        }

        EmitQueueBalancerChangedDiagnostics();

        var acquired = newQueues.Except(oldQueues).Count();
        if (acquired > 0)
        {
            EmitQueueLeasesAcquiredDiagnostics(acquired);
        }

        var released = oldQueues.Except(newQueues).Count();
        if (released > 0)
        {
            EmitQueueLeasesReleasedDiagnostics(released);
        }
    }

    private void EmitQueueLeasesAcquiredDiagnostics(int acquired)
    {
        if (!s_diagnosticListener.IsEnabled(OrleansStreamingDiagnostics.EventNames.QueueLeasesAcquired))
        {
            return;
        }

        s_diagnosticListener.Write(
            OrleansStreamingDiagnostics.EventNames.QueueLeasesAcquired,
            new QueueLeasesAcquiredEvent(
                _name,
                SiloAddress,
                acquired,
                _myQueues.Count,
                _responsibility));
    }

    private void EmitQueueLeasesReleasedDiagnostics(int released)
    {
        if (!s_diagnosticListener.IsEnabled(OrleansStreamingDiagnostics.EventNames.QueueLeasesReleased))
        {
            return;
        }

        s_diagnosticListener.Write(
            OrleansStreamingDiagnostics.EventNames.QueueLeasesReleased,
            new QueueLeasesReleasedEvent(
                _name,
                SiloAddress,
                released,
                _myQueues.Count,
                _responsibility));
    }
}
