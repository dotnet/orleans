using Microsoft.Extensions.DependencyInjection;

namespace FasterSample.Core.Pipelines
{
    public static class AsyncPipelineDependencyInjectionExtensions
    {
        public static IServiceCollection AddAsyncPipelineFactory(this IServiceCollection services)
        {
            return services
                .AddSingleton<IAsyncPipelineFactory, AsyncPipelineFactory>();
        }
    }
}