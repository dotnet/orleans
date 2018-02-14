
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    /// <summary>
    /// Performance tuning options. 
    /// </summary>
    public class PerformanceTuningOptions
    {
        #region ServicePointManager related settings
        /// <summary>
        /// <see cref="ServicePointManager"/> related settings
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

    /// <summary>
    /// Options formatter for PerformanceTuningOptions
    /// </summary>
    public class PerformanceTuningOptionsFormatter : IOptionFormatter<PerformanceTuningOptions>
    {
        public string Name => nameof(PerformanceTuningOptions);

        private PerformanceTuningOptions options;
        public PerformanceTuningOptionsFormatter(IOptions<PerformanceTuningOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.DefaultConnectionLimit), this.options.DefaultConnectionLimit),
                OptionFormattingUtilities.Format(nameof(this.options.Expect100Continue), this.options.Expect100Continue),
                OptionFormattingUtilities.Format(nameof(this.options.UseNagleAlgorithm), this.options.UseNagleAlgorithm),
                OptionFormattingUtilities.Format(nameof(this.options.MinDotNetThreadPoolSize),this.options.MinDotNetThreadPoolSize)
            };
        }
    }
}
