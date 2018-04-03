
namespace Orleans.Configuration
{
    public class LoadSheddingOptions
    {
        /// <summary>
        /// Specifies whether or not load shedding in the client gateway and stream providers is enabled.
        /// The default value is false, meaning that load shedding is disabled.
        /// </summary>
        public bool LoadSheddingEnabled { get; set; }

        /// <summary>
        /// Specifies the system load, in CPU%, at which load begins to be shed.
        /// Note that this value is in %, so valid values range from 1 to 100, and a reasonable value is
        /// typically between 80 and 95.
        /// This value is ignored if load shedding is disabled, which is the default.
        /// If load shedding is enabled and this attribute does not appear, then the default limit is 95%.
        /// </summary>
        public int LoadSheddingLimit { get; set; } = DEFAULT_LOAD_SHEDDING_LIMIT;
        public const int DEFAULT_LOAD_SHEDDING_LIMIT = 95;
    }
}
