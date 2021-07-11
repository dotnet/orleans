using Microsoft.Extensions.DependencyInjection;

namespace FasterSample.Core.RandomGenerators
{
    public static class RandomGeneratorDependencyInjectionExtensions
    {
        public static IServiceCollection AddRandomGenerator(this IServiceCollection services)
        {
            return services
                .AddSingleton<IRandomGenerator, RandomGenerator>();
        }
    }
}