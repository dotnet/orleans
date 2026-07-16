using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans.TestingHost;

/// <summary>
/// Test helpers for overriding the per-area <see cref="TimeProvider"/> instances resolved via keyed dependency
/// injection. See <see cref="TimeProviderNames"/>.
/// </summary>
public static class TimeProviderTestingExtensions
{
    /// <summary>
    /// Overrides the <see cref="TimeProvider"/> for all background/infrastructure areas
    /// (see <see cref="TimeProviderNames.BackgroundAreas"/>) while leaving grain-facing areas unchanged.
    /// </summary>
    /// <remarks>
    /// This is intended for tests which install a controllable clock as the default <see cref="TimeProvider"/> to
    /// drive grain timers deterministically, but need the silo's background maintenance timers to remain on real
    /// time so that advancing the clock does not resume those loops inline.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="timeProvider">The time provider to use for background areas.</param>
    public static IServiceCollection UseTimeProviderForBackgroundAreas(this IServiceCollection services, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(timeProvider);
        foreach (var area in TimeProviderNames.BackgroundAreas)
        {
            services.AddKeyedSingleton(area, timeProvider);
        }

        return services;
    }
}
