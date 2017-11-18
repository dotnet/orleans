
namespace Orleans.Runtime
{
    /// <summary>
    /// Stages of a silo's lifecycle.
    /// </summary>
    public enum SiloLifecycleStage
    {
        /// <summary>
        /// Initialize silo runtime
        /// </summary>
        RuntimeInitialize = 1000,

        /// <summary>
        /// Start runtime services
        /// </summary>
        RuntimeServices = 2000,

        /// <summary>
        /// Start application layer services
        /// </summary>
        ApplicationServices = 3000,

        /// <summary>
        /// Silo is active and available to service requests
        /// </summary>
        SiloActive = 4000,
    }
}
