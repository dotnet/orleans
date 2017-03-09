namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Indicates the reason for removing activations from the directory.
    /// This influences the conditions that are applied when determining whether or not to remove an entry.
    /// </summary>
    internal enum UnregistrationCause : byte
    {
        /// <summary>
        /// Remove the directory entry forcefully, without any conditions
        /// </summary>
        Force,

        /// <summary>
        /// Remove the directory entry only if it points to an activation in a different cluster
        /// </summary>
        CacheInvalidation,

        /// <summary>
        /// Remove the directory entry only if it is not too fresh (to avoid races on new registrations)
        /// </summary>
        NonexistentActivation
    }
}