using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Runtime
{
    /// <summary>
    /// Configures the Orleans cluster client.
    /// </summary>
    public class ClusterClientOptions
    {
        /// <summary>
        /// Gets or sets the cluster identity. This used to be called DeploymentId before Orleans 2.0 name.
        /// </summary>
        public string ClusterId { get; set; }
    }

    public class ClusterClientOptionsFormatter : IOptionFormatter<ClusterClientOptions>
    {
        public string Category { get; }

        public string Name => nameof(ClusterClientOptions);
        private ClusterClientOptions options;
        public ClusterClientOptionsFormatter(IOptions<ClusterClientOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(options.ClusterId), options.ClusterId)
            };
        }
    }
}
