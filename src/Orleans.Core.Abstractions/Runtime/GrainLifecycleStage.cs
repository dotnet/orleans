namespace Orleans.Runtime
{
    /// <summary>
    /// Stages of a grains lifecycle.
    /// TODO: Add more later, see ActivationInitializationStage
    /// Full grain lifecycle, including register, state setup, and 
    ///   stream cleanup should all eventually be triggered by the 
    ///   grain lifecycle.
    /// </summary>
    public static class GrainLifecycleStage
    {
        /// <summary>
        /// First valid stage in grain's lifecycle.
        /// </summary>
        public const int First = int.MinValue;

        /// <summary>
        /// Setup grain state prior to activation.
        /// </summary>
        public const int SetupState = 1000;

        /// <summary>
        /// Activate grain.
        /// </summary>
        public const int Activate = 2000;

        /// <summary>
        /// Last valid stage in grain's lifecycle.
        /// </summary>
        public const int Last = int.MaxValue;
    }
}
