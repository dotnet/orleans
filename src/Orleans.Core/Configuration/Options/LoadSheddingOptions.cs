
using Orleans.Statistics;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options relating to load shedding.
    /// </summary>
    public class LoadSheddingOptions
    {
        /// <summary>
        /// The default value for <see cref="LoadSheddingLimit"/>.
        /// </summary>
        internal const int DefaultLoadSheddingLimit = 95;

        /// <summary>
        /// Gets or sets a value indicating whether load shedding in the client gateway and stream providers is enabled.
        /// The default value is <see langword="false"/>, meaning that load shedding is disabled.
        /// In addition to enabling this option, a valid <see cref="IHostEnvironmentStatistics"/> implementation must be registered on gateway hosts to enable this functionality.
        /// </summary>
        /// <value>Load shedding is disabled by default.</value>
        public bool LoadSheddingEnabled { get; set; }

        /// <summary>
        /// Gets or sets the CPU utilization, expressed as a value between <c>0</c> and <c>100</c>, at which load shedding begins.
        /// Note that this value is in %, so valid values range from 1 to 100, and a reasonable value is
        /// typically between 80 and 95.
        /// This value is ignored if load shedding is disabled, which is the default.
        /// If load shedding is enabled and this attribute does not appear, then the default limit is 95%.
        /// </summary>
        /// <value>Load shedding begins at a CPU utilization of 95% by default.</value>
        public int LoadSheddingLimit { get; set; } = DefaultLoadSheddingLimit;
    }
}
