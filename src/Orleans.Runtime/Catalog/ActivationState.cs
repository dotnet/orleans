namespace Orleans.Runtime
{
    internal enum ActivationState
    {
        /// <summary>
        /// Activation is being created
        /// </summary>
        Creating,
        
        /// <summary>
        /// Activation is in the middle of activation process.
        /// </summary>
        Activating,
        
        /// <summary>
        /// Activation was successfully activated and ready to process requests.
        /// </summary>
        Valid,
        
        /// <summary>
        /// Activation is in the middle of deactivation process.
        /// </summary>
        Deactivating,
        
        /// <summary>
        /// Tombstone for an activation which has terminated.
        /// </summary>
        Invalid
    }
}
