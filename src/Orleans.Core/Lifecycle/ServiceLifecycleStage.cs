
namespace Orleans
{
    /// <summary>
    /// Lifecycle stages of an Orleans client or silo.
    /// </summary>
    public static class ServiceLifecycleStage
    {
        /// <summary>
        /// First valid stage in service's lifecycle
        /// </summary>
        public const int First = int.MinValue;

        /// <summary>
        /// Initialize runtime
        /// </summary>
        public const int RuntimeInitialize = 2000;

        /// <summary>
        /// Start runtime services
        /// </summary>
        public const int RuntimeServices = 4000;

        /// <summary>
        /// Initialize runtime storage
        /// </summary>
        public const int RuntimeStorageServices = 6000;

        /// <summary>
        /// Start runtime services
        /// </summary>
        public const int RuntimeGrainServices = 8000;

        /// <summary>
        /// After runtime services have started.
        /// </summary>
        public const int AfterRuntimeGrainServices = 8100;

        /// <summary>
        /// Start application layer services
        /// </summary>
        public const int ApplicationServices = 10000;

        /// <summary>
        /// Service will be active after this step.
        /// It should only be used by the membership oracle 
        /// and the gateway, no other component should run
        /// at this stage
        /// </summary>
        public const int BecomeActive = Active-1;

        /// <summary>
        /// Service is active.
        /// </summary>
        public const int Active = 20000;

        /// <summary>
        /// Last valid stage in service's lifecycle
        /// </summary>
        public const int Last = int.MaxValue;
    }
}
