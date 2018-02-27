using Orleans.Configuration;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.TestingHost.Legacy
{
    public class TestClusterLegacyUtils
    {
        /// <summary>
        /// Get the timeout value to use to wait for the silo liveness sub-system to detect and act on any recent cluster membership changes.
        /// <seealso cref="WaitForLivenessToStabilizeAsync"/>
        /// </summary>
        public static TimeSpan GetLivenessStabilizationTime(GlobalConfiguration config, bool didKill = false)
        {
            var membershipOptions = new MembershipOptions()
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
            return TestCluster.GetLivenessStabilizationTime(membershipOptions, didKill);
        }
    }
}
