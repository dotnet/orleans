using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using Orleans.Diagnostics;

namespace Orleans.Runtime;

internal static class DeploymentLoadPublisherDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansPlacementDiagnostics.ListenerName);

    internal static void EmitClusterStatisticsRefreshed(SiloAddress siloAddress, ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> periodicStats)
    {
        if (!Listener.IsEnabled(OrleansPlacementDiagnostics.EventNames.ClusterStatisticsRefreshed))
        {
            return;
        }

        Emit(Listener, siloAddress, periodicStats);

        static void Emit(DiagnosticListener listener, SiloAddress siloAddress, ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> periodicStats)
        {
            listener.Write(OrleansPlacementDiagnostics.EventNames.ClusterStatisticsRefreshed, new ClusterStatisticsRefreshedEvent(
                siloAddress,
                periodicStats.Count,
                periodicStats.Values.Sum(statistics => statistics.ActivationCount),
                periodicStats));
        }
    }

    internal static void EmitStatisticsPublished(SiloAddress siloAddress, SiloRuntimeStatistics statistics, bool loadSheddingEnabled)
    {
        if (!Listener.IsEnabled(OrleansPlacementDiagnostics.EventNames.StatisticsPublished))
        {
            return;
        }

        Emit(Listener, siloAddress, statistics, loadSheddingEnabled);

        static void Emit(DiagnosticListener listener, SiloAddress siloAddress, SiloRuntimeStatistics statistics, bool loadSheddingEnabled)
        {
            listener.Write(OrleansPlacementDiagnostics.EventNames.StatisticsPublished, new SiloStatisticsPublishedEvent(
                siloAddress,
                statistics.ActivationCount,
                statistics.RecentlyUsedActivationCount,
                loadSheddingEnabled && statistics.IsOverloaded,
                DateTime.UtcNow,
                statistics));
        }
    }

    internal static void EmitStatisticsReceived(SiloAddress sourceSiloAddress, SiloAddress observerSiloAddress, SiloRuntimeStatistics statistics)
    {
        if (sourceSiloAddress == observerSiloAddress
            || !Listener.IsEnabled(OrleansPlacementDiagnostics.EventNames.StatisticsReceived))
        {
            return;
        }

        Emit(Listener, sourceSiloAddress, observerSiloAddress, statistics);

        static void Emit(DiagnosticListener listener, SiloAddress sourceSiloAddress, SiloAddress observerSiloAddress, SiloRuntimeStatistics statistics)
        {
            listener.Write(OrleansPlacementDiagnostics.EventNames.StatisticsReceived, new SiloStatisticsReceivedEvent(
                sourceSiloAddress,
                observerSiloAddress,
                statistics.ActivationCount,
                statistics.RecentlyUsedActivationCount,
                statistics.IsOverloaded,
                DateTime.UtcNow,
                statistics));
        }
    }

    internal static void EmitStatisticsRemoved(SiloAddress removedSiloAddress, SiloAddress observerSiloAddress, string reason)
    {
        if (!Listener.IsEnabled(OrleansPlacementDiagnostics.EventNames.StatisticsRemoved))
        {
            return;
        }

        Emit(Listener, removedSiloAddress, observerSiloAddress, reason);

        static void Emit(DiagnosticListener listener, SiloAddress removedSiloAddress, SiloAddress observerSiloAddress, string reason)
        {
            listener.Write(OrleansPlacementDiagnostics.EventNames.StatisticsRemoved, new SiloStatisticsRemovedEvent(
                removedSiloAddress,
                observerSiloAddress,
                reason));
        }
    }
}
