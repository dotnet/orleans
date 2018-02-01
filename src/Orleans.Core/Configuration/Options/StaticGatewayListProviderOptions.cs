using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration.Options
{
    /// <summary>
    /// Options for Configure StaticGatewayListProvider
    /// </summary>
    public class StaticGatewayListProviderOptions
    {
        /// <summary>
        /// Static gateways to use
        /// </summary>
        public IList<Uri> Gateways { get; set; } = new List<Uri>();
    }

    public class StaticGatewayListProviderOptionsFormatter : IOptionFormatter<StaticGatewayListProviderOptions>
    {
        public string Category { get; }

        public string Name => nameof(StaticGatewayListProviderOptions);

        private StaticGatewayListProviderOptions options;
        public StaticGatewayListProviderOptionsFormatter(IOptions<StaticGatewayListProviderOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
                {OptionFormattingUtilities.Format(nameof(options.Gateways), string.Join(",", options.Gateways))};
        }
    }
}
