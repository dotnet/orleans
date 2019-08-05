namespace Orleans.Runtime
{
    internal enum ActivationState
    {
        /// <summary>
        /// Activation is being created
        /// </summary>
        Create,
        ///// <summary>
        ///// Activation is in the middle of activation process.
        ///// </summary>
        Activating,
        /// <summary>
        /// Activation was successfully activated and ready to process requests.
        /// </summary>
        Valid,
        ///// <summary>
        ///// Activation is in the middle of deactivation process.
        ///// </summary>
        Deactivating,
        /// <summary>
        /// Tombstone for activation that is not registered in the Directory
        /// </summary>
        Invalid,
        /// <summary>
        /// Tombstone for activation that threw an exception in OnActivateAsync
        /// </summary>
        FailedToActivate,
    }
}
