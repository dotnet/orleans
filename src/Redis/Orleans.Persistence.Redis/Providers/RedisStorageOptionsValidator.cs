using Orleans.Runtime;

namespace Orleans.Persistence
{
    internal class RedisStorageOptionsValidator : IConfigurationValidator
    {
        private readonly RedisStorageOptions _options;
        private readonly string _name;

        public RedisStorageOptionsValidator(RedisStorageOptions options, string name)
        {
            _options = options;
            _name = name;
        }

        public void ValidateConfiguration()
        {
            if (_options.ConfigurationOptions == null)
            {
                throw new OrleansConfigurationException($"Invalid configuration for {nameof(RedisGrainStorage)} with name {_name}. {nameof(RedisStorageOptions)}.{nameof(_options.ConfigurationOptions)} is required.");
            }
        }
    }
}