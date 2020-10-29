using Microsoft.Extensions.DependencyInjection;

namespace FasterSample.Core.Clocks
{
    public static class SystemClockDependencyInjectionExtensions
    {
        public static IServiceCollection AddSystemClock(this IServiceCollection services)
        {
            return services
                .AddSingleton<ISystemClock, SystemClock>();
        }
    }
}