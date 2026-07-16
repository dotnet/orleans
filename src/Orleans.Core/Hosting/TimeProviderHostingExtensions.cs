using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Runtime;

namespace Orleans.Hosting;

/// <summary>
/// Wires up per-area keyed <see cref="TimeProvider"/> resolution. See <see cref="TimeProviderNames"/>.
/// </summary>
internal static class TimeProviderHostingExtensions
{
    /// <summary>
    /// Registers a catch-all keyed <see cref="TimeProvider"/> (<see cref="KeyedService.AnyKey"/>) which resolves to
    /// the unkeyed default <see cref="TimeProvider"/>. Consumers resolve their area's clock via
    /// <c>[FromKeyedServices(TimeProviderNames.X)]</c>; unless an area has been explicitly overridden, this fallback
    /// supplies the default provider, so behavior is unchanged in production.
    /// </summary>
    internal static IServiceCollection AddKeyedTimeProviders(this IServiceCollection services)
    {
        services.TryAddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        return services;
    }
}
