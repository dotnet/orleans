using System;
using Orleans.Core;

namespace Orleans.Runtime
{
    [Serializable]
    public class DetailedGrainStatistic
    {
        /// <summary>
        /// The type of the grain for this DetailedGrainStatistic.
        /// </summary>
        public string GrainType { get; set; }

        /// <summary>
        /// The silo address for this DetailedGrainStatistic.
        /// </summary>
        public SiloAddress SiloAddress { get; set; }

        /// <summary>
        /// Unique Id for the grain.
        /// </summary>
        public IGrainIdentity GrainIdentity { get; set; }

        /// <summary>
        /// The grains Category
        /// </summary>
        public string Category { get; set; }
    }
}