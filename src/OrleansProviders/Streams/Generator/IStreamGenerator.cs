
using System;
using System.Collections.Generic;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// Interface of generators used by the GeneratorStreamProvider.  Any method of generating events
    ///  must conform to this interface to be used by the GeneratorStreamProvider.
    /// </summary>
    public interface IStreamGenerator
    {
        /// <summary>
        /// Tries to get an event, if the generator is configured to generate any at this time
        /// </summary>
        bool TryReadEvents(DateTime utcNow, int maxCount, out List<IBatchContainer> events);

        /// <summary>
        /// Configures the generator
        /// </summary>
        /// <param name="serviceProvider"></param>
        /// <param name="generatorConfig"></param>
        void Configure(IServiceProvider serviceProvider, IStreamGeneratorConfig generatorConfig);
    }

    /// <summary>
    /// Interface of configuration for generators used by the GeneratorStreamProvider.  This interface covers
    ///   the minimal set of information the stream provider needs to configure a generator to generate data.  Generators should
    ///   add any additional configuration information needed to it's implementation of this interface.
    /// </summary>
    public interface IStreamGeneratorConfig
    {
        /// <summary>
        /// Stream generator type
        /// </summary>
        Type StreamGeneratorType { get; }
    }
}
