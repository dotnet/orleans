
namespace Orleans.Configuration
{
    /// <summary>
    /// Performance tuning options. 
    /// </summary>
    public class PerformanceTuningOptions
    {
        /// <summary>
        /// ServicePointManager related settings
        /// </summary>
        public int DefaultConnectionLimit { get; set; } = DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT;
        public static readonly int DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT = 200;

        public bool Expect100Continue { get; set; }

        public bool UseNagleAlgorithm { get; set; }

        /// <summary>
        /// Minimum number of .NET worker threads.
        /// </summary>
        public int MinDotNetThreadPoolSize { get; set; }

        /// <summary>
        /// Minimum number of I/O completion port threads.
        /// </summary>
        public int MinIOThreadPoolSize { get; set; }
    }
}
