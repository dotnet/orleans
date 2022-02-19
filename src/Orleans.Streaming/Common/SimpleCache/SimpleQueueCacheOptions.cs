using System;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration options for the simple queue cache.
    /// </summary>
    public class SimpleQueueCacheOptions
    {
        /// <summary>
        /// Gets or sets the size of the cache.
        /// </summary>
        /// <value>The size of the cache.</value>
        public int CacheSize { get; set; } = DEFAULT_CACHE_SIZE;

        /// <summary>
        /// The default value of <see cref="CacheSize"/>.
        /// </summary>
        public const int DEFAULT_CACHE_SIZE = 4096;
    }

    /// <summary>
    /// Validates <see cref="SimpleQueueCacheOptions"/>.
    /// </summary>
    public class SimpleQueueCacheOptionsValidator : IConfigurationValidator
    {
        private readonly SimpleQueueCacheOptions options;
        private readonly string name;

        /// <summary>
        /// Initializes a new instance of the <see cref="SimpleQueueCacheOptionsValidator"/> class.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="name">The name.</param>
        private SimpleQueueCacheOptionsValidator(SimpleQueueCacheOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            if(options.CacheSize <= 0)
                throw new OrleansConfigurationException($"{nameof(SimpleQueueCacheOptions)} on stream provider {this.name} is invalid. {nameof(SimpleQueueCacheOptions.CacheSize)} must be larger than zero");
        }

        /// <summary>
        /// Creates a new <see cref="SimpleQueueCacheOptionsValidator"/> instance.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="name">The provider name.</param>
        /// <returns>A new <see cref="SimpleQueueCacheOptionsValidator"/> instance.</returns>
        public static IConfigurationValidator Create(IServiceProvider services, string name)
        {
            SimpleQueueCacheOptions queueCacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            return new SimpleQueueCacheOptionsValidator(queueCacheOptions, name);
        }
    }
}
