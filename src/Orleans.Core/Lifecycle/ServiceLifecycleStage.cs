namespace Orleans
{
    /// <summary>
    /// Lifecycle stages of an orlean service.  Cluster Client, or Silo
    /// </summary>
    public static class ServiceLifecycleStage
    {
        /// <summary>
        /// First valid stage in service's lifecycle
        /// </summary>
        public const int First = int.MinValue + 1;

        /// <summary>
        /// Initialize runtime
        /// </summary>
        public const int RuntimeInitialize = 2_000;

        /// <summary>
        /// Start runtime services
        /// </summary>
        public const int RuntimeServices = 4_000;

        /// <summary>
        /// Initialize grain services
        /// </summary>
        public const int RuntimeGrainServices = 6_000;

        /// <summary>
        /// Transition into the Joining state in membership.
        /// After this stage:
        /// <list type="bullet">
        ///   <item>
        ///     <description>Other silos are able to see that this silo is joining the cluster.</description>
        ///   </item>
        ///   <item>
        ///     <description>Grain placement can be made via the grain directory on other silos only.</description>
        ///   </item>
        /// </list>
        /// </summary>
        public const int BecomeJoining = 8_000;
        
        /// <summary>
        /// Start application layer services.
        /// </summary>
        public const int ApplicationServices = 10_000;

        /// <summary>
        /// Transition into the Active state in membership.
        ///
        /// Before this stage:
        /// <list type="bullet">
        ///   <item>
        ///     <description>Services which are required for grain activation must be available.</description>
        ///   </item>
        /// </list>
        /// 
        /// After this stage:
        /// <list type="bullet">
        ///   <item>
        ///     <description>Grain placement can be made via the grain directory on this silos as well as other silos.</description>
        ///   </item>
        ///   <item>
        ///     <description>Grains can be activated on this silo.</description>
        ///   </item>
        /// </list>
        /// </summary>
        public const int BecomeActive = 12_000;

        /// <summary>
        /// Grain calls are enabled after this stage.
        /// </summary>
        public const int EnableGrainCalls = BecomeActive + 1;

        /// <summary>
        /// Initialize legacy providers.
        /// </summary>
        public const int LegacyProviders = EnableGrainCalls + 1;

        /// <summary>
        /// Service is active.
        /// </summary>
        public const int Active = 20_000;

        /// <summary>
        /// Last valid stage in service's lifecycle
        /// </summary>
        public const int Last = int.MaxValue - 1;
    }
}
