using System;
using Microsoft.Extensions.DependencyInjection;

namespace FasterSample.Core.Pipelines
{
    internal class AsyncPipelineFactory : IAsyncPipelineFactory
    {
        private readonly IServiceProvider _provider;

        public AsyncPipelineFactory(IServiceProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public IAsyncPipeline Create(int capacity) => ActivatorUtilities.CreateInstance<AsyncPipeline>(_provider, capacity);
    }
}