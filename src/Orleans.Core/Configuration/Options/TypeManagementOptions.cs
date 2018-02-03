using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;

namespace Orleans.Hosting
{
    public class TypeManagementOptions
    {
        /// <summary>
        /// The number of seconds to refresh the cluster grain interface map
        /// </summary>
        public TimeSpan TypeMapRefreshInterval { get; set; } = DEFAULT_REFRESH_CLUSTER_INTERFACEMAP_TIME;
        public static readonly TimeSpan DEFAULT_REFRESH_CLUSTER_INTERFACEMAP_TIME = TimeSpan.FromMinutes(1);
    }

    public class TypeManagementOptionsFormatter : IOptionFormatter<TypeManagementOptions>
    {
        public string Category { get; }

        public string Name => nameof(TypeManagementOptions);

        private TypeManagementOptions options;
        public TypeManagementOptionsFormatter(IOptions<TypeManagementOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(options.TypeMapRefreshInterval),options.TypeMapRefreshInterval),
            };
        }
    }
}
