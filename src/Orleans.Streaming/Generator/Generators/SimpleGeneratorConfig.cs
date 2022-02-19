
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
    [GenerateSerializer]
    public class SimpleGeneratorOptions : IStreamGeneratorConfig
    {
        /// <summary>
        /// Gets or sets the stream namespace.
        /// </summary>
        /// <value>The stream namespace.</value>
        [Id(0)]
        public string StreamNamespace { get; set; }

        /// <summary>
        /// Gets the stream generator type
        /// </summary>
        /// <value>The type of the stream generator.</value>
        public Type StreamGeneratorType => typeof (SimpleGenerator);

        /// <summary>
        /// Gets or sets the number of events to generate.
        /// </summary>
        /// <value>The number of events to generate.</value>
        [Id(1)]
        public int EventsInStream { get; set; } = DEFAULT_EVENTS_IN_STREAM;

        /// <summary>
        /// The default number of events to generate.
        /// </summary>
        public const int DEFAULT_EVENTS_IN_STREAM = 100;
    }
}
