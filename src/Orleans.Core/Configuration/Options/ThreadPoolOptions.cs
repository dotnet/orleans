
using Microsoft.Extensions.Options;
using System.Collections.Generic;

namespace Orleans.Hosting
{
    public class ThreadPoolOptions
    {
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
                OptionFormattingUtilities.Format(nameof(options.MinDotNetThreadPoolSize),options.MinDotNetThreadPoolSize),
            };
        }
    }
}
