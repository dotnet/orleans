
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Settings for tuning service point for faster access to azure storage.
    /// NOTE: Should not be part of orleans core.
    /// TODO: Remove - jbragg
    /// </summary>
    public class ServicePointOptions
    {
        public int DefaultConnectionLimit { get; set; } = DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT;
        public static readonly int DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT = ThreadPoolOptions.DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE;

        public bool Expect100Continue { get; set; }

        public bool UseNagleAlgorithm { get; set; }
    }

    public class ServicePointOptionsFormatter : IOptionFormatter<ServicePointOptions>
    {
        public string Category { get; }

        public string Name => nameof(ServicePointOptions);

        private ServicePointOptions options;
        public ServicePointOptionsFormatter(IOptions<ServicePointOptions> options)
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
            };
        }
    }
}
