using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Silo configuration options.
    /// </summary>
    public class SiloOptions
    {
        /// <summary>
        /// Gets or sets the silo name.
        /// </summary>
        public string SiloName { get; set; }

        /// <summary>
        /// Gets or sets the cluster identity. This used to be called DeploymentId before Orleans 2.0 name.
        /// </summary>
        public string ClusterId { get; set; }

        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment, where as <see cref="ClusterId"/> might not.
        /// </summary>
        public Guid ServiceId { get; set; }

        public bool FastKillOnCancelKeyPress { get; set; } = DEFAULT_FAST_KILL_ON_CANCEL;
        public const bool DEFAULT_FAST_KILL_ON_CANCEL = true;
    }

    public class SiloOptionsFormatter : IOptionFormatter<SiloOptions>
    {
        public string Category { get; }

        public string Name => nameof(SiloOptions);

        private SiloOptions options;
        public SiloOptionsFormatter(IOptions<SiloOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(options.SiloName),options.SiloName),
                OptionFormattingUtilities.Format(nameof(options.ClusterId), options.ClusterId),
                OptionFormattingUtilities.Format(nameof(options.ServiceId), options.ServiceId),
                OptionFormattingUtilities.Format(nameof(options.FastKillOnCancelKeyPress), options.FastKillOnCancelKeyPress)
            };
        }
    }
}