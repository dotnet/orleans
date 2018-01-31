
using System.Xml;

namespace Orleans.Runtime
{
    /// <summary>
    /// Stages of a silo's lifecycle.
    /// </summary>
    public static class SiloLifecycleStage
    {
        /// <summary>
        /// First ring in silo's lifecycle
        /// </summary>
        public const int First = int.MinValue;

        /// <summary>
        /// Initialize silo runtime
        /// </summary>
        public const int RuntimeInitialize = 1000;

        /// <summary>
        /// Start runtime services
        /// </summary>
        public const int RuntimeServices = 2000;

        /// <summary>
        /// Initialize runtime storage
        /// </summary>
        public const int RuntimeStorageServices = 3000;

        /// <summary>
        /// Start runtime services
        /// </summary>
        public const int RuntimeGrainServices = 4000;

        /// <summary>
        /// Start application layer services
        /// </summary>
        public const int ApplicationServices = 5000;

        /// <summary>
        /// Silo is active and available to service requests
        /// </summary>
        public const int SiloActive = 6000;
    }
}
