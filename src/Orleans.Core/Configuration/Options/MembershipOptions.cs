
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Settings for cluster membership.
    /// </summary>
    public class MembershipOptions
    {
        /// <summary>
        /// The number of missed "I am alive" updates  in the table from a silo that causes warning to be logged. Does not impact the liveness protocol.
        /// </summary>
        public int NumMissedTableIAmAliveLimit { get; set; } = DEFAULT_LIVENESS_NUM_TABLE_I_AM_ALIVE_LIMIT;
        public const int DEFAULT_LIVENESS_NUM_TABLE_I_AM_ALIVE_LIMIT = 2;

        /// <summary>
        /// Global switch to disable silo liveness protocol (should be used only for testing).
        /// The LivenessEnabled attribute, if provided and set to "false", suppresses liveness enforcement.
        /// If a silo is suspected to be dead, but this attribute is set to "false", the suspicions will not propagated to the system and enforced,
        /// This parameter is intended for use only for testing and troubleshooting.
        /// In production, liveness should always be enabled.
        /// Default is true (eanabled)
        /// </summary>
        public bool LivenessEnabled { get; set; } = DEFAULT_LIVENESS_ENABLED;
        public const bool DEFAULT_LIVENESS_ENABLED = true;

        /// <summary>
        /// The number of seconds to periodically probe other silos for their liveness or for the silo to send "I am alive" heartbeat  messages about itself.
        /// </summary>
        public TimeSpan ProbeTimeout { get; set; }
        public static readonly TimeSpan DEFAULT_LIVENESS_PROBE_TIMEOUT = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The number of seconds to periodically fetch updates from the membership table.
        /// </summary>
        public TimeSpan TableRefreshTimeout { get; set; }
        public static readonly TimeSpan DEFAULT_LIVENESS_TABLE_REFRESH_TIMEOUT = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Expiration time in seconds for death vote in the membership table.
        /// </summary>
        public TimeSpan DeathVoteExpirationTimeout { get; set; }
        public static readonly TimeSpan DEFAULT_LIVENESS_DEATH_VOTE_EXPIRATION_TIMEOUT = TimeSpan.FromSeconds(120);

        /// <summary>
        /// The number of seconds to periodically write in the membership table that this silo is alive. Used ony for diagnostics.
        /// </summary>
        public TimeSpan IAmAliveTablePublishTimeout { get; set; }
        public static readonly TimeSpan DEFAULT_LIVENESS_I_AM_ALIVE_TABLE_PUBLISH_TIMEOUT = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The number of seconds to attempt to join a cluster of silos before giving up.
        /// </summary>
        public TimeSpan MaxJoinAttemptTime { get; set; }
        public static readonly TimeSpan DEFAULT_LIVENESS_MAX_JOIN_ATTEMPT_TIME = TimeSpan.FromMinutes(5); // 5 min

        /// <summary>
        /// The expected size of a cluster. Need not be very accurate, can be an overestimate.
        /// </summary>
        public int ExpectedClusterSize { get; set; }

        /// <summary>
        /// Whether new silo that joins the cluster has to validate the initial connectivity with all other Active silos.
        /// </summary>
        public bool ValidateInitialConnectivity { get; set; } = DEFAULT_VALIDATE_INITIAL_CONNECTIVITY;
        public const bool DEFAULT_VALIDATE_INITIAL_CONNECTIVITY = true;

        /// <summary>
        /// Whether to use the gossip optimization to speed up spreading liveness information.
        /// </summary>
        public bool UseLivenessGossip { get; set; } = DEFAULT_LIVENESS_USE_LIVENESS_GOSSIP;
        public const bool DEFAULT_LIVENESS_USE_LIVENESS_GOSSIP = true;

        /// <summary>
        /// The number of silos each silo probes for liveness.
        /// </summary>
        public int NumProbedSilos { get; set; } = DEFAULT_LIVENESS_NUM_PROBED_SILOS;
        public const int DEFAULT_LIVENESS_NUM_PROBED_SILOS = 3;

        /// <summary>
        /// The number of missed "I am alive" heartbeat messages from a silo or number of un-replied probes that lead to suspecting this silo as dead.
        /// </summary>
        public int NumMissedProbesLimit { get; set; } = DEFAULT_LIVENESS_NUM_MISSED_PROBES_LIMIT;
        public const int DEFAULT_LIVENESS_NUM_MISSED_PROBES_LIMIT = 3;

        /// <summary>
        /// The number of non-expired votes that are needed to declare some silo as dead (should be at most NumMissedProbesLimit)
        /// </summary>
        public int NumVotesForDeathDeclaration { get; set; } = DEFAULT_LIVENESS_NUM_VOTES_FOR_DEATH_DECLARATION;
        public const int DEFAULT_LIVENESS_NUM_VOTES_FOR_DEATH_DECLARATION = 2;

        /// <summary>
        /// TEST ONLY - Do not modify in production environments
        /// </summary>
        public bool IsRunningAsUnitTest { get; set; } = false;
    }

    public class MembershipOptionsFormatter : IOptionFormatter<MembershipOptions>
    {
        public string Name => nameof(MembershipOptions);
        private MembershipOptions options;
        public MembershipOptionsFormatter(IOptions<MembershipOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.NumMissedTableIAmAliveLimit), this.options.NumMissedTableIAmAliveLimit),
                OptionFormattingUtilities.Format(nameof(this.options.LivenessEnabled), this.options.LivenessEnabled),
                OptionFormattingUtilities.Format(nameof(this.options.ProbeTimeout), this.options.ProbeTimeout),
                OptionFormattingUtilities.Format(nameof(this.options.TableRefreshTimeout), this.options.TableRefreshTimeout),
                OptionFormattingUtilities.Format(nameof(this.options.DeathVoteExpirationTimeout), this.options.DeathVoteExpirationTimeout),
                OptionFormattingUtilities.Format(nameof(this.options.IAmAliveTablePublishTimeout), this.options.IAmAliveTablePublishTimeout),
                OptionFormattingUtilities.Format(nameof(this.options.MaxJoinAttemptTime), this.options.MaxJoinAttemptTime),
                OptionFormattingUtilities.Format(nameof(this.options.ExpectedClusterSize), this.options.ExpectedClusterSize),
                OptionFormattingUtilities.Format(nameof(this.options.ValidateInitialConnectivity), this.options.ValidateInitialConnectivity),
                OptionFormattingUtilities.Format(nameof(this.options.UseLivenessGossip), this.options.UseLivenessGossip),
                OptionFormattingUtilities.Format(nameof(this.options.NumProbedSilos), this.options.NumProbedSilos),
                OptionFormattingUtilities.Format(nameof(this.options.NumMissedProbesLimit), this.options.NumMissedProbesLimit),
                OptionFormattingUtilities.Format(nameof(this.options.NumVotesForDeathDeclaration), this.options.NumVotesForDeathDeclaration),
                OptionFormattingUtilities.Format(nameof(this.options.IsRunningAsUnitTest), this.options.IsRunningAsUnitTest),
            };
        }
    }
}
