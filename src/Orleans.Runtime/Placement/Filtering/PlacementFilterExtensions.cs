using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Placement.Filtering;

public static class PlacementFilterExtensions
{
    /// <summary>
    /// Configures a <typeparamref name="TFilter"/> for filtering candidate grain placements.
    /// </summary>
    /// <typeparam name="TFilter">The placement filter.</typeparam>
    /// <typeparam name="TDirector">The placement filter director.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="strategyLifetime">The lifetime of the placement strategy.</param>
    /// <returns>The service collection.</returns>
    public static void AddPlacementFilter<TFilter, TDirector>(this IServiceCollection services, ServiceLifetime strategyLifetime)
        where TFilter : PlacementFilterStrategy
        where TDirector : class, IPlacementFilterDirector
    {
        services.Add(ServiceDescriptor.DescribeKeyed(typeof(PlacementFilterStrategy), typeof(TFilter).Name, typeof(TFilter), strategyLifetime));
        services.AddKeyedSingleton<IPlacementFilterDirector, TDirector>(typeof(TFilter));
    }

}