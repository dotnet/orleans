using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using System;

namespace Orleans.Streams
{
    public class StreamCheckpointerConfigurationValidator : IConfigurationValidator
    {
        private readonly IServiceProvider _services;
        private readonly string _name;

        public StreamCheckpointerConfigurationValidator(IServiceProvider services, string name)
        {
            _services = services;
            _name = name;
        }

        public void ValidateConfiguration()
        {
            var checkpointerFactory = _services.GetKeyedService<IStreamQueueCheckpointerFactory>(_name);
            if (checkpointerFactory == null)
            {
                throw new OrleansConfigurationException($"No IStreamQueueCheckpointer is configured with PersistentStreamProvider {_name}. Please configure one.");
            }
        }
    }
}
