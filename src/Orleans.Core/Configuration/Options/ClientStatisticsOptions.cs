
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Statistics output related options for cluster client.
    /// </summary>
    public class ClientStatisticsOptions : StatisticsOptions
    {
    }

    public class ClientStatisticsOptionsFormatter : StatisticsOptionsFormatter, IOptionFormatter<ClientStatisticsOptions>
    {
        public string Category { get; }

        public string Name => nameof(ClientStatisticsOptions);

        private ClientStatisticsOptions options;

        public ClientStatisticsOptionsFormatter(IOptions<ClientStatisticsOptions> options)
            : base(options.Value)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return base.FormatSharedOptions();
        }
    }

}
