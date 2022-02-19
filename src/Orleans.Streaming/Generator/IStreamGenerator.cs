
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
        /// <param name="utcNow">The current UTC time.</param>
        /// <param name="maxCount">The maximum number of events to read.</param>
        /// <param name="events">The events.</param>
        /// <returns><see langword="true" /> if events were read, <see langword="false" /> otherwise.</returns>
        bool TryReadEvents(DateTime utcNow, int maxCount, out List<IBatchContainer> events);

        /// <summary>
        /// Configures the stream generator.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="generatorConfig">The generator configuration.</param>
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
        /// Gets the stream generator type
        /// </summary>
        Type StreamGeneratorType { get; }
    }
}
