using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Statistics;

namespace Orleans.Core.Messaging
{
    /// <summary>
    /// Determines whether or not the process is overloaded.
    /// </summary>
    internal class OverloadDetector
    {
        private readonly float cpuLimit;
        private readonly float memoryLimit;
        private readonly IEnvironmentStatistics _environmentStatistics;
        
        public OverloadDetector(IEnvironmentStatistics environmentStatistics, IOptions<LoadSheddingOptions> loadSheddingOptions)
        {
            _environmentStatistics = environmentStatistics;
            cpuLimit = loadSheddingOptions.Value.LoadSheddingLimit;
            memoryLimit = loadSheddingOptions.Value.MemoryLoadLimit;
        }

        /// <summary>
        /// Gets or sets a value indicating whether overload detection is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Returns <see langword="true"/> if this process is overloaded, <see langword="false"/> otherwise.
        /// </summary>
        public bool Overloaded => Enabled && (IsMemoryPressure() || _environmentStatistics.CpuUsagePercentage > cpuLimit);
        
        private bool IsMemoryPressure()
        {
            var info = GC.GetGCMemoryInfo();
            return info.MemoryLoadBytes >= memoryLimit * Math.Min(info.TotalAvailableMemoryBytes, info.HighMemoryLoadThresholdBytes);
        }
    }
}