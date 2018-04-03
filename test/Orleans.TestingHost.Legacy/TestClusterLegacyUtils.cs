using Orleans.Configuration;
using Orleans.Runtime.Configuration;
using System;

namespace Orleans.TestingHost.Legacy
{
    public class TestClusterLegacyUtils
    {
        /// <summary>
        /// Get the timeout value to use to wait for the silo liveness sub-system to detect and act on any recent cluster membership changes.
        /// </summary>
        public static TimeSpan GetLivenessStabilizationTime(GlobalConfiguration config, bool didKill = false)
        {
            var clusterMembershipOptions = new ClusterMembershipOptions()
            {
                NumMissedTableIAmAliveLimit = config.NumMissedTableIAmAliveLimit,
                LivenessEnabled = config.LivenessEnabled,
                ProbeTimeout = config.ProbeTimeout,
                TableRefreshTimeout = config.TableRefreshTimeout,
                DeathVoteExpirationTimeout = config.DeathVoteExpirationTimeout,
                IAmAliveTablePublishTimeout = config.IAmAliveTablePublishTimeout,
                MaxJoinAttemptTime = config.MaxJoinAttemptTime,
                ExpectedClusterSize = config.ExpectedClusterSize,
                ValidateInitialConnectivity = config.ValidateInitialConnectivity,
                NumMissedProbesLimit = config.NumMissedProbesLimit,
                UseLivenessGossip = config.UseLivenessGossip,
                NumProbedSilos = config.NumProbedSilos,
                NumVotesForDeathDeclaration = config.NumVotesForDeathDeclaration,
            };
            return TestCluster.GetLivenessStabilizationTime(clusterMembershipOptions, didKill);
        }
    }
}
