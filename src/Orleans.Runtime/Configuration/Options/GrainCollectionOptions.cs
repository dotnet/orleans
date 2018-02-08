using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo options for grain garbage collection.
    /// </summary>
    public class GrainCollectionOptions
    {
        /// <summary>
        /// Regulates the periodic collection of inactive grains.
        /// </summary>
        public TimeSpan CollectionQuantum { get; set; } = DEFAULT_COLLECTION_QUANTUM;
        public static readonly TimeSpan DEFAULT_COLLECTION_QUANTUM = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Default period of inactivity necessary for a grain to be available for collection and deactivation.
        /// </summary>
        public TimeSpan CollectionAge { get; set; } = DEFAULT_COLLECTION_AGE_LIMIT;
        public static readonly TimeSpan DEFAULT_COLLECTION_AGE_LIMIT = TimeSpan.FromHours(2);

        /// <summary>
        /// Period of inactivity necessary for a grain to be available for collection and deactivation by grain type.
        /// </summary>
        public Dictionary<string, TimeSpan> ClassSpecificCollectionAge { get; set; } = new Dictionary<string, TimeSpan>();
    }

    public class GrainCollectionOptionsFormatter : IOptionFormatter<GrainCollectionOptions>
    {
        public string Category { get; }

        public string Name => nameof(GrainCollectionOptions);
        private GrainCollectionOptions options;
        public GrainCollectionOptionsFormatter(IOptions<GrainCollectionOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            var formated = new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.CollectionQuantum), this.options.CollectionQuantum),
                OptionFormattingUtilities.Format(nameof(this.options.CollectionAge), this.options.CollectionAge),
            };
            foreach(KeyValuePair<string, TimeSpan> classCollectionAge in this.options.ClassSpecificCollectionAge)
            {
                formated.Add(OptionFormattingUtilities.Format($"{nameof(this.options.CollectionAge)}.{classCollectionAge.Key}", classCollectionAge.Value));
            }
            return formated;
        }
    }
}
