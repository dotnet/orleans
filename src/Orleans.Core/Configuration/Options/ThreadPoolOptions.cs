
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Thread pool settings
    /// NOTE: Should this be in orleans core? - jbragg
    /// </summary>
    public class ThreadPoolOptions
    {
        /// <summary>
        /// Minimum number of DotNet threads.
        /// </summary>
        public int MinDotNetThreadPoolSize { get; set; } = DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE;
        public const int DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE = 200;
    }

    public class ThreadPoolOptionsFormatter : IOptionFormatter<ThreadPoolOptions>
    {
        public string Category { get; }

        public string Name => nameof(ThreadPoolOptions);

        private ThreadPoolOptions options;
        public ThreadPoolOptionsFormatter(IOptions<ThreadPoolOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.MinDotNetThreadPoolSize),this.options.MinDotNetThreadPoolSize),
            };
        }
    }
}
