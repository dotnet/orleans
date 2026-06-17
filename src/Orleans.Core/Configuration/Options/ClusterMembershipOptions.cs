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
        public int NumMissedTableIAmAliveLimit { get; set; } = 3;

        /// <summary>
        /// Gets or sets a value indicating whether the silo liveness protocol is enabled (should be disabled only for testing).
        /// If a silo is suspected to be down, but this property is set to <see langword="false"/>, the suspicions will not be propagated to the system or enforced.
        /// This property is intended for use only for testing and troubleshooting.
        /// In production, liveness should always be enabled.
        /// </summary>
        /// <value>Liveness is enabled by default.</value>
        public bool LivenessEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets both the period between sending a liveness probe to any given host as well as the timeout for each probe.
        /// </summary>
        /// <value>Probes time out and a new probe is sent every 5 seconds by default.</value>
        public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the period between fetching updates from the membership table.
        /// </summary>
        /// <value>The membership table is refreshed every 60 seconds by default.</value>
        public TimeSpan TableRefreshTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets the expiration time for votes in the membership table.
        /// </summary>
        /// <value>Votes expire after 2 minutes by default.</value>
        public TimeSpan DeathVoteExpirationTimeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Gets or sets the period between updating this silo's heartbeat in the membership table.
        /// </summary>
        /// <remarks>
        /// These heartbeats are largely for diagnostic purposes, however they are also used to identify stale active
        /// entries in the membership table. This value multiplied by <see cref="NumMissedTableIAmAliveLimit"/>
        /// determines how long an active membership entry can go without a heartbeat update before it is treated as stale.
        /// </remarks>
        /// <value>Publish an update every 30 seconds by default.</value>
        public TimeSpan IAmAliveTablePublishTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the maximum amount of time to attempt to join a cluster before giving up.
        /// </summary>
        /// <value>Attempt to join for 5 minutes before giving up by default.</value>
        public TimeSpan MaxJoinAttemptTime { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets a value indicating whether membership updates are disseminated between hosts using gossip.
        /// </summary>
        /// <value>Membership updates are disseminated using gossip by default.</value>
        public bool UseLivenessGossip { get; set; } = true;

        /// <summary>
        /// Gets or sets the number of silos each silo probes for liveness.
        /// </summary>
        /// <remarks>
        /// This determines how many hosts each host will monitor by default.
        /// A low value, such as 3, is generally sufficient and allows for prompt removal of another silo in the event that it stops functioning.
        /// Monitoring is not expensive, however, and a higher value improves recovery during sudden changes in cluster size.
        /// When a silo becomes suspicious of another silo, additional silos may begin to probe that silo to speed up the detection of non-functioning silos.
        /// </remarks>
        /// <value>Each silo will actively monitor up to 10 other silos by default.</value>
        public int NumProbedSilos { get; set; } = 10;

        /// <summary>
        /// Gets or sets the number of missed probe requests from a silo that lead to suspecting this silo as down.
        /// </summary>
        /// <value>A silo will be suspected as being down if three probes are missed, by default.</value>
        public int NumMissedProbesLimit { get; set; } = 3;

        /// <summary>
        /// Gets or sets the number of non-expired votes that are needed to declare some silo as down (should be at most <see cref="NumProbedSilos"/>).
        /// </summary>
        /// <value>Two votes are sufficient for a silo to be declared as down, by default.</value>
        public int NumVotesForDeathDeclaration { get; set; } = 2;

        /// <summary>
        /// Gets or sets the period of time after which membership entries for defunct silos are eligible for removal.
        /// Valid only if <see cref="DefunctSiloCleanupPeriod"/> is not <see langword="null" />.
        /// </summary>
        /// <value>Defunct silos are eligible for removal from membership after one week by default.</value>
        public TimeSpan DefunctSiloExpiration { get; set; } = TimeSpan.FromDays(7);

        /// <summary>
        /// Gets or sets a value indicating whether defunct silo entries older than <see cref="DefunctSiloExpiration" /> are removed.
        /// Cleanup is attempted when membership changes are observed and the current membership snapshot contains expired
        /// non-active entries, or when this period has elapsed since the last cleanup call.
        /// Set this value to <see langword="null"/> to disable expiration-based cleanup.
        /// </summary>
        /// <value>Expiration-based cleanup is enabled by default.</value>
        public TimeSpan? DefunctSiloCleanupPeriod { get; set; } = TimeSpan.FromHours(1);

        /// <summary>
        /// Gets or sets the maximum number of defunct silo entries to retain in the membership table.
        /// When this limit is exceeded, the first active silo, selected by natural <see cref="Orleans.Runtime.SiloAddress"/>
        /// sort order, asynchronously removes the oldest excess defunct entries from the membership table.
        /// Set this value to <see langword="null"/> to retain all defunct entries and disable threshold-based cleanup.
        /// </summary>
        /// <value>Membership retains up to 25 defunct silo entries by default.</value>
        public int? MaxDefunctSiloEntries { get; set; } = 25;

        /// <summary>
        /// Gets the period after which a silo's membership entry is considered stale if it has not updated its heartbeat in the membership table.
        /// </summary>
        internal TimeSpan AllowedIAmAliveMissPeriod => IAmAliveTablePublishTimeout.Multiply(NumMissedTableIAmAliveLimit);

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

        /// <summary>
        /// Gets or sets a value indicating whether to enable membership eviction of silos when they remain in the <c>Joining</c> or <c>Created</c> state for longer than <see cref="MaxJoinAttemptTime"/>.
        /// </summary>
        public bool EvictWhenMaxJoinAttemptTimeExceeded { get; set; } = true;
    }
}
