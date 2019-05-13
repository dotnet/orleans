
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Providers.Streams.Generator;

namespace Orleans.Hosting
{
    /// <summary>
    /// Simple generator configuration class.
    /// This class is used to configure a generator stream provider to generate streams using the SimpleGenerator
    /// </summary>
    [Serializable]
    public class SimpleGeneratorOptions : IStreamGeneratorConfig
    {
        /// <summary>
        /// Stream namespace
        /// </summary>
        public string StreamNamespace { get; set; }

        /// <summary>
        /// Stream generator type
        /// </summary>
        public Type StreamGeneratorType => typeof (SimpleGenerator);

        /// <summary>
        /// Nuber of events to generate on this stream
        /// </summary>
        public int EventsInStream { get; set; } = DEFAULT_EVENTS_IN_STREAM;
        public const int DEFAULT_EVENTS_IN_STREAM = 100;
    }

    public class SimpleGeneratorOptionsFormatterResolver : IOptionFormatterResolver<SimpleGeneratorOptions>
    {
        private IOptionsMonitor<SimpleGeneratorOptions> optionsMonitor;

        public SimpleGeneratorOptionsFormatterResolver(IOptionsMonitor<SimpleGeneratorOptions> optionsMonitor)
        {
            this.optionsMonitor = optionsMonitor;
        }

        public IOptionFormatter<SimpleGeneratorOptions> Resolve(string name)
        {
            return new Formatter(name, this.optionsMonitor.Get(name));
        }

        private class Formatter : IOptionFormatter<SimpleGeneratorOptions>
        {
            private SimpleGeneratorOptions options;

            public string Name { get; }

            public Formatter(string name, SimpleGeneratorOptions options)
            {
                this.options = options;
                this.Name = OptionFormattingUtilities.Name<SimpleGeneratorOptions>(name);
            }

            public IEnumerable<string> Format()
            {
                return new List<string> 
                {
                    OptionFormattingUtilities.Format(nameof(this.options.StreamNamespace), this.options.StreamNamespace),
                    OptionFormattingUtilities.Format(nameof(this.options.StreamGeneratorType), this.options.StreamGeneratorType),
                    OptionFormattingUtilities.Format(nameof(this.options.EventsInStream), this.options.EventsInStream),
                };
            }
        }
    }
}
