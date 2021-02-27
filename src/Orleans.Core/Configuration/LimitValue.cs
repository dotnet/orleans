using System;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Data class encapsulating details of a particular system limit.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class LimitValue
    {
        /// <summary>
        /// Name of this Limit value
        /// </summary>
        [Id(1)]
        public string Name { get; set; }

        /// <summary>
        /// 'Soft" limit threshold value for this Limit, after which Warnings will start to be generated
        /// </summary>
        [Id(2)]
        public int SoftLimitThreshold { get; set; }

        /// <summary>
        /// 'Hard' limit threshold value, after which Errors will start to be generated and action take (for example, rejecting new request messages, etc) 
        /// to actively reduce the limit value back to within thresholds.
        /// </summary>
        [Id(3)]
        public int HardLimitThreshold { get; set; }

        public override string ToString()
        {
            return string.Format("Limit:{0},SoftLimitThreshold={1},HardLimitThreshold={2}",
                Name, SoftLimitThreshold, HardLimitThreshold);
        }
    }
}
