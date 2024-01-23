using System;

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
        internal const int DefaultCpuThreshold = 95;

        /// <summary>
        /// The default value for <see cref="MemoryThreshold"/>.
        /// </summary>
        internal const int DefaultMemoryThreshold = 90;

        /// <summary>
        /// Gets or sets a value indicating whether load shedding in the client gateway and stream providers is enabled.
        /// The default value is <see langword="false"/>, meaning that load shedding is disabled.
        /// </summary>
        /// <value>Load shedding is disabled by default.</value>
        public bool LoadSheddingEnabled { get; set; }

        /// <summary>
        /// Gets or sets the CPU utilization, expressed as a value between <c>0</c> and <c>100</c>, at which load shedding begins.
        /// Note that this value is in %, so valid values range from 1 to 100, and a reasonable value is typically between 80 and 95.
        /// This value is ignored if load shedding is disabled, which is the default.
        /// </summary>
        /// <value>Load shedding begins at a CPU utilization of 95% by default, if load shedding is enabled.</value>
        /// <remarks>This property is deprecated. Use <see cref="CpuThreshold"/> instead.</remarks>
        [Obsolete($"Use {nameof(CpuThreshold)} instead.", error: true)]
        public int LoadSheddingLimit { get => CpuThreshold; set => CpuThreshold = value; }

        /// <summary>
        /// Gets or sets the CPU utilization, expressed as a value between <c>0</c> and <c>100</c>, at which load shedding begins.
        /// Note that this value is in %, so valid values range from 1 to 100, and a reasonable value is typically between 80 and 95.
        /// This value is ignored if load shedding is disabled, which is the default.
        /// </summary>
        /// <value>Load shedding begins at a CPU utilization of 95% by default, if load shedding is enabled.</value>
        public int CpuThreshold { get; set; } = DefaultCpuThreshold;

        /// <summary>
        /// Gets or sets the memory utilization, expressed as a value between <c>0</c> and <c>100</c>, at which load shedding begins.
        /// Note that this value is in %, so valid values range from 1 to 100, and a reasonable value is typically between 80 and 95.
        /// This value is ignored if load shedding is disabled, which is the default.
        /// </summary>
        /// <value>Load shedding begins at a memory utilization of 90% by default, if load shedding is enabled.</value>
        public int MemoryThreshold { get; set; } = DefaultMemoryThreshold;
    }
}
