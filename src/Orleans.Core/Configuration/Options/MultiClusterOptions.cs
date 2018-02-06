using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Options for configuring multi-cluster support
    /// </summary>
    public class MultiClusterOptions
    {
        public static class BuiltIn
        {
            /// <summary>Default value to allow discrimination of override values.</summary>
            public const string NotSpecified = "NotSpecified";

            /// <summary>An Azure Table serving as a channel. </summary>
            public const string AzureTable = "AzureTable";
        }

        /// <summary>
        /// Whether this cluster is configured to be part of a multi-cluster network
        /// </summary>
        public bool HasMultiClusterNetwork { get; set; } = false;

        /// <summary>
        ///A list of cluster ids, to be used if no multi-cluster configuration is found in gossip channels.
        /// </summary>
        public IList<string> DefaultMultiCluster { get; set; } = new List<string>();

        /// <summary>
        /// The maximum number of silos per cluster should be designated to serve as gateways.
        /// </summary>
        public int MaxMultiClusterGateways { get; set; } = 10;

        /// <summary>
        /// The time between background gossips.
        /// </summary>
        public TimeSpan BackgroundGossipInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Whether to use the global single instance protocol as the default
        /// multi-cluster registration strategy.
        /// </summary>
        public bool UseGlobalSingleInstanceByDefault { get; set; } = true;

        /// <summary>
        /// The number of quick retries before going into DOUBTFUL state.
        /// </summary>
        public int GlobalSingleInstanceNumberRetries { get; set; } = 10;

        /// <summary>
        /// The time between the slow retries for DOUBTFUL activations.
        /// </summary>
        public TimeSpan GlobalSingleInstanceRetryInterval { get; set; } = DEFAULT_GLOBAL_SINGLE_INSTANCE_RETRY_INTERVAL;
        public static readonly TimeSpan DEFAULT_GLOBAL_SINGLE_INSTANCE_RETRY_INTERVAL = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Inter-cluster gossip channels.
        /// </summary>
        public Dictionary<string, string> GossipChannels { get; set; } = new Dictionary<string, string>();
    }

    public class MultiClusterOptionsFormatter : IOptionFormatter<MultiClusterOptions>
    {
        public string Category { get; }

        public string Name => nameof(MultiClusterOptions);
        private MultiClusterOptions options;
        public MultiClusterOptionsFormatter(IOptions<MultiClusterOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            var formated = new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.HasMultiClusterNetwork), this.options.HasMultiClusterNetwork),
                OptionFormattingUtilities.Format(nameof(this.options.DefaultMultiCluster), string.Join(";", this.options.DefaultMultiCluster)),
                OptionFormattingUtilities.Format(nameof(this.options.MaxMultiClusterGateways), this.options.MaxMultiClusterGateways),
                OptionFormattingUtilities.Format(nameof(this.options.BackgroundGossipInterval), this.options.BackgroundGossipInterval),
                OptionFormattingUtilities.Format(nameof(this.options.UseGlobalSingleInstanceByDefault), this.options.UseGlobalSingleInstanceByDefault),
                OptionFormattingUtilities.Format(nameof(this.options.GlobalSingleInstanceNumberRetries), this.options.GlobalSingleInstanceNumberRetries),
                OptionFormattingUtilities.Format(nameof(this.options.GlobalSingleInstanceRetryInterval), this.options.GlobalSingleInstanceRetryInterval),
            };
            foreach (KeyValuePair<string, string> channel in this.options.GossipChannels)
            {
                formated.Add(OptionFormattingUtilities.Format($"{nameof(this.options.GossipChannels)}.{channel.Key}", channel.Value));
            }
            return formated;
        }
    }
}
