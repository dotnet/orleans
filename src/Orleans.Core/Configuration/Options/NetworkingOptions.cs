using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configures networking options.
    /// </summary>
    public class NetworkingOptions
    {
        /// <summary>
        /// The OpenConnectionTimeout attribute specifies the timeout before a connection open is assumed to have failed
        /// </summary>
        public TimeSpan OpenConnectionTimeout { get; set; } = Constants.DEFAULT_OPENCONNECTION_TIMEOUT;

        /// <summary>
        /// The MaxSocketAge attribute specifies how long to keep an open socket before it is closed.
        /// Default is TimeSpan.MaxValue (never close sockets automatically, unles they were broken).
        /// </summary>
        public TimeSpan MaxSocketAge { get; set; } = TimeSpan.MaxValue;
    }

    public class NetworkingOptionsFormatter : IOptionFormatter<NetworkingOptions>
    {
        public string Category { get; }

        public string Name => nameof(NetworkingOptions);

        private NetworkingOptions options;
        public NetworkingOptionsFormatter(IOptions<NetworkingOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.OpenConnectionTimeout),this.options.OpenConnectionTimeout),
                OptionFormattingUtilities.Format(nameof(this.options.MaxSocketAge), this.options.MaxSocketAge)
            };
        }
    }
}
