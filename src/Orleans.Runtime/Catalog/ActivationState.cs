namespace Orleans.Runtime
{
    internal enum ActivationState
    {
        /// <summary>
        /// Activation is being created
        /// </summary>
        Create,
        
        //// <summary>
        //// Activation is in the middle of activation process.
        //// </summary>
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
        Invalid,
        
        /// <summary>
        /// Tombstone for an activation that threw an exception during activation.
        /// </summary>
        FailedToActivate,
    }
}
