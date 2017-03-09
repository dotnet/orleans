using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Snapshot of current statistics for a given grain type.
    /// </summary>
    [Serializable]
    internal class GrainStatistic
    {
        /// <summary>
        /// The type of the grain for this GrainStatistic.
        /// </summary>
        public string GrainType { get; set; }

        /// <summary>
        /// Number of grains of a this type.
        /// </summary>
        public int GrainCount { get; set; }

        /// <summary>
        /// Number of activation of a agrain of this type.
        /// </summary>
        public int ActivationCount { get; set; }

        /// <summary>
        /// Number of silos that have activations of this grain type.
        /// </summary>
        public int SiloCount { get; set; }

        /// <summary>
        /// Returns the string representatio of this GrainStatistic.
        /// </summary>
        public override string ToString()
        {
            return string.Format("GrainStatistic: GrainType={0} NumSilos={1} NumGrains={2} NumActivations={3} ", GrainType, SiloCount, GrainCount, ActivationCount);
        }
    }
}