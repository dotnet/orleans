namespace Orleans.Runtime
{
    /// <summary>
    /// Stages of a grains lifecycle.
    /// TODO: Add more later, see ActivationInitializationStage
    /// Full grain lifecycle, including register, state setup, and 
    ///   stream cleanup should all eventually be triggered by the 
    ///   grain lifecycle.
    /// </summary>
    public enum GrainLifecycleStage
    {
        /// <summary>
        /// Setup grain state prior to activation 
        /// </summary>
        SetupState = 1000,  

        /// <summary>
        /// Activate grain
        /// </summary>
        Activate = 2000
    }
}
