using System;
using System.Diagnostics;
using System.Linq;
using Orleans.Diagnostics;

#nullable disable
namespace Orleans.Runtime
{
    internal sealed partial class DeploymentLoadPublisher
    {
        private static readonly DiagnosticListener _diagnosticListener = new(OrleansPlacementDiagnostics.ListenerName);

        private void EmitClusterStatisticsRefreshedDiagnostics()
        {
            if (!_diagnosticListener.IsEnabled(OrleansPlacementDiagnostics.EventNames.ClusterStatisticsRefreshed))
            {
                return;
            }

            _diagnosticListener.Write(OrleansPlacementDiagnostics.EventNames.ClusterStatisticsRefreshed, new ClusterStatisticsRefreshedEvent(
                _siloDetails.SiloAddress,
                _periodicStats.Count,
                _periodicStats.Values.Sum(statistics => statistics.ActivationCount)));
        }

        private void EmitStatisticsPublishedDiagnostics(SiloRuntimeStatistics statistics)
        {
            if (!_diagnosticListener.IsEnabled(OrleansPlacementDiagnostics.EventNames.StatisticsPublished))
            {
                return;
            }

            _diagnosticListener.Write(OrleansPlacementDiagnostics.EventNames.StatisticsPublished, new SiloStatisticsPublishedEvent(
                _siloDetails.SiloAddress,
                statistics.ActivationCount,
                statistics.RecentlyUsedActivationCount,
                _loadSheddingOptions.Value.LoadSheddingEnabled && statistics.IsOverloaded,
                DateTime.UtcNow));
        }

        private void EmitStatisticsReceivedDiagnostics(SiloAddress siloAddress, SiloRuntimeStatistics statistics)
        {
            if (siloAddress == _siloDetails.SiloAddress
                || !_diagnosticListener.IsEnabled(OrleansPlacementDiagnostics.EventNames.StatisticsReceived))
            {
                return;
            }

            _diagnosticListener.Write(OrleansPlacementDiagnostics.EventNames.StatisticsReceived, new SiloStatisticsReceivedEvent(
                siloAddress,
                _siloDetails.SiloAddress,
                statistics.ActivationCount,
                statistics.RecentlyUsedActivationCount,
                statistics.IsOverloaded,
                DateTime.UtcNow));
        }

        private void EmitStatisticsRemovedDiagnostics(SiloAddress updatedSilo)
        {
            if (!_diagnosticListener.IsEnabled(OrleansPlacementDiagnostics.EventNames.StatisticsRemoved))
            {
                return;
            }

            _diagnosticListener.Write(OrleansPlacementDiagnostics.EventNames.StatisticsRemoved, new SiloStatisticsRemovedEvent(
                updatedSilo,
                _siloDetails.SiloAddress,
                "SiloTerminating"));
        }
    }
}
