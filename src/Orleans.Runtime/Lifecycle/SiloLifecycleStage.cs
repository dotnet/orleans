
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
        RuntimeInitialize = (1 << 10),

        /// <summary>
        /// Start runtime services
        /// </summary>
        RuntimeServices = RuntimeInitialize + (1 << 10),

        /// <summary>
        /// Start application layer services
        /// </summary>
        ApplicationServices = RuntimeServices + (1 << 10),

        /// <summary>
        /// Silo is active and available to service requests
        /// </summary>
        SiloActive = ApplicationServices + (1 << 10),
    }
}
