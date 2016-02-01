namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Status of a directory entry with respect to multi-cluster registration
    /// </summary>
    internal enum MultiClusterStatus : byte
    {
        /// <summary>
        /// Registration is owned by this cluster.
        /// </summary>
        Owned,

        /// <summary>
        /// Failed to contact one or more clusters while registering, so may be a duplicate.
        /// </summary>
        Doubtful,

        /// <summary>
        /// Cached reference to a registration owned by a remote cluster.
        /// </summary>
        Cached,

        /// <summary>
        /// The cluster is in the process of checking remote clusters for existing registrations.
        /// </summary>
        RequestedOwnership,

        /// <summary>
        /// The cluster lost a race condition.
        /// </summary>
        RaceLoser,
    }
}
