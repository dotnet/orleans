using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for MemoryGrainStorage
    /// </summary>
    public class MemoryGrainStorageOptions
    {
        /// <summary>
        /// Default number of queue storage grains.
        /// </summary>
        public const int NumStorageGrainsDefaultValue = 10;
        /// <summary>
        /// Number of store grains to use.
        /// </summary>
        public int NumStorageGrains { get; set; } = NumStorageGrainsDefaultValue;

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        /// <summary>
        /// Default init stage
        /// </summary>
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;
    }

    public class MemoryGrainStorageOptionsValidator : IConfigurationValidator
    {
        private readonly MemoryGrainStorageOptions options;
        private readonly string name;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        /// <param name="name">The option name to be validated.</param>
        public MemoryGrainStorageOptionsValidator(MemoryGrainStorageOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }

        public void ValidateConfiguration()
        {
            if (this.options.NumStorageGrains <= 0)
                throw new OrleansConfigurationException(
                    $"Configuration for {nameof(MemoryGrainStorage)} {name} is invalid. {nameof(this.options.NumStorageGrains)} must be larger than 0.");
            if(this.options.InitStage < ServiceLifecycleStage.RuntimeGrainServices)
                throw new OrleansConfigurationException(
                   $"Configuration for {nameof(MemoryGrainStorage)} {name} is invalid. {nameof(this.options.InitStage)} must be larger than {ServiceLifecycleStage.RuntimeGrainServices} since " +
                   $"{nameof(MemoryGrainStorage)} depends on {nameof(MemoryStorageGrain)} to have grain environment to finish set up.");
        }
    }
}
