
namespace Orleans.Configuration
{
    /// <summary>
    /// Performance tuning options. 
    /// </summary>
    public class PerformanceTuningOptions
    {
#region ServicePointManager related settings
        /// <summary>
        /// ServicePointManager related settings
        /// </summary>
        public int DefaultConnectionLimit { get; set; } = DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT;
        public static readonly int DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT = DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE;

        public bool Expect100Continue { get; set; }

        public bool UseNagleAlgorithm { get; set; }
#endregion
        /// <summary>
        /// Minimum number of DotNet threads.
        /// </summary>
        public int MinDotNetThreadPoolSize { get; set; } = DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE;
        public const int DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE = 200;
    }
}
