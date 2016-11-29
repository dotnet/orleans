namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Status of a directory entry with respect to multi-cluster registration
    /// </summary>
    internal enum GrainDirectoryEntryStatus : byte
    {
        /// <summary>
        /// Used as a return value, indicating no registration present in directory.
        /// </summary>
        Invalid,

        //--- the following state is used for cluster-local grains

        /// <summary>
        /// Used for normal grains that have no multi-cluster semantics.
        /// </summary>
        ClusterLocal,


        //--- the following states are used for global-single-instance grain, the meaning is as defined by the GSI protocol

        /// <summary>
        /// Registration that is owned by this cluster.
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
