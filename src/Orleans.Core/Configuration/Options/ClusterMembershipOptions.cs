using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Settings for cluster membership.
    /// </summary>
    public class ClusterMembershipOptions
    {
        /// <summary>
        /// Gets or sets the number of missed "I am alive" updates in the table from a silo that causes warning to be logged.
        /// </summary>
        /// <seealso cref="IAmAliveTablePublishTimeout"/>
        public int NumMissedTableIAmAliveLimit { get; set; } = 2;

        /// <summary>
        /// Gets or sets a value indicating whether to disable silo liveness protocol (should be used only for testing).
        /// If a silo is suspected to be down, but this attribute is set to <see langword="false"/>, the suspicions will not propagated to the system and enforced.
        /// This parameter is intended for use only for testing and troubleshooting.
        /// In production, liveness should always be enabled.
        /// </summary>
        /// <value>Liveness is enabled by default.</value>
        public bool LivenessEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets both the period between sending a liveness probe to any given host as well as the timeout for each probe.
        /// </summary>
        /// <value>Probes timeout and a new probe is sent every 5 seconds by default.</value>
        public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the period between fetching updates from the membership table.
        /// </summary>
        /// <value>The membership table is refreshed every 60 seconds by default.</value>
        public TimeSpan TableRefreshTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets the expiration time in seconds for votes in the membership table.
        /// </summary>
        /// <value>Votes expire after 2 minutes by default.</value>
        public TimeSpan DeathVoteExpirationTimeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Gets or sets the period between updating this silo's heartbeat in the membership table.
        /// </summary>
        /// <remarks>
        /// These heartbeats are largely for diagnostic purposes, however they are also used to ignore entries
        /// in the membership table in the event of a total cluster reset. This value multiplied by <see cref="NumMissedTableIAmAliveLimit"/>
        /// is used to skip hosts in the membership table when performing an initial connectivity check upon startup.
        /// </remarks>
        /// <value>Publish an update every 5 minutes by default.</value>
        public TimeSpan IAmAliveTablePublishTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets the maximum amount of time to attempt to join a cluster before giving up.
        /// </summary>
        /// <value>Attempt to join for 5 minutes before giving up by default.</value>
        public TimeSpan MaxJoinAttemptTime { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets a value indicating whether gossip membership updates between hosts.
        /// </summary>
        /// <value>Membership updates are disseminated using gossip by default.</value>
        public bool UseLivenessGossip { get; set; } = true;

        /// <summary>
        /// Gets or sets the number of silos each silo probes for liveness.
        /// </summary>
        /// <remarks>
        /// This determines how many hosts each host will monitor by default.
        /// A low value, such as the default value of three, is generally sufficient and allows for prompt removal of another silo in the event that it stops functioning.
        /// When a silo becomes suspicious of another silo, additional silos may begin to probe that silo to speed up the detection of non-functioning silos.
        /// </remarks>
        /// <value>Each silo will actively monitor up to three other silos by default.</value>
        public int NumProbedSilos { get; set; } = 3;

        /// <summary>
        /// Gets or sets the number of missed probe requests from a silo that lead to suspecting this silo as down.
        /// </summary>
        /// <value>A silo will be suspected as being down if three probes are missed, by default.</value>
        public int NumMissedProbesLimit { get; set; } = 3;

        /// <summary>
        /// Gets or sets the number of non-expired votes that are needed to declare some silo as down (should be at most <see cref="NumMissedProbesLimit"/>)
        /// </summary>
        /// <value>Two votes are sufficient for a silo to be declared as down, by default.</value>
        public int NumVotesForDeathDeclaration { get; set; } = 2;

        /// <summary>
        /// Gets or sets the period of time after which membership entries for defunct silos are eligible for removal.
        /// Valid only if <see cref="DefunctSiloCleanupPeriod"/> is not <see langword="null" />.
        /// </summary>
        /// <value>Defunct silos are removed from membership after one week by default.</value>
        public TimeSpan DefunctSiloExpiration { get; set; } = TimeSpan.FromDays(7);

        /// <summary>
        /// Gets or sets the duration between membership table cleanup operations. When this period elapses, all defunct silo
        /// entries older than <see cref="DefunctSiloExpiration" /> are removed. This value is per-silo.
        /// </summary>
        /// <value>Membership is cleared of expired, defunct silos every hour, by default.</value>
        public TimeSpan? DefunctSiloCleanupPeriod { get; set; } = TimeSpan.FromHours(1);

        /// <summary>
        /// /// Gets the period after which a silo is ignored for initial connectivity validation if it has not updated its heartbeat in the silo membership table.
        /// </summary>
        internal TimeSpan AllowedIAmAliveMissPeriod => this.IAmAliveTablePublishTimeout.Multiply(this.NumMissedTableIAmAliveLimit);

        /// <summary>
        /// Gets the amount of time to wait for the cluster membership system to terminate during shutdown.
        /// </summary>
        internal static TimeSpan ClusteringShutdownGracePeriod => TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the period between self-tests to log local health degradation status.
        /// </summary>
        /// <value>The local host will perform a self-test every ten seconds by default.</value>
        public TimeSpan LocalHealthDegradationMonitoringPeriod { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets a value indicating whether to extend the effective <see cref="ProbeTimeout"/> value based upon current local health degradation.
        /// </summary>
        public bool ExtendProbeTimeoutDuringDegradation { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to enable probing silos indirectly, via other silos.
        /// </summary>
        public bool EnableIndirectProbes { get; set; } = true;
    }
}
