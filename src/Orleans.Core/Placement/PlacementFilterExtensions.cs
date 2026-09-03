using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Placement;

/// <summary>
/// Provides extension methods for registering placement filters.
/// </summary>
public static class PlacementFilterExtensions
{
    /// <summary>
    /// Registers a placement filter strategy and its director for filtering candidate grain placements.
    /// </summary>
    /// <typeparam name="TFilter">The placement filter strategy type.</typeparam>
    /// <typeparam name="TDirector">The director which applies <typeparamref name="TFilter"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="strategyLifetime">The service lifetime of the placement filter strategy.</param>
    /// <returns>The provided service collection.</returns>
    public static IServiceCollection AddPlacementFilter<TFilter, TDirector>(this IServiceCollection services, ServiceLifetime strategyLifetime)
        where TFilter : PlacementFilterStrategy, new()
        where TDirector : class, IPlacementFilterDirector
    {
        services.Add(ServiceDescriptor.DescribeKeyed(typeof(PlacementFilterStrategy), typeof(TFilter).Name, typeof(TFilter), strategyLifetime));
        services.AddKeyedSingleton<IPlacementFilterDirector, TDirector>(typeof(TFilter));

        return services;
    }
<<<<<<< HEAD
||||||| parent of 82a763ec4 (style: format solution whitespace)

}
=======

>>>>>>> 82a763ec4 (style: format solution whitespace)
}
