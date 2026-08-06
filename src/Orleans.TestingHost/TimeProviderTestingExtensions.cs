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
    /// Overrides the <see cref="TimeProvider"/> for every keyed area except the grain-facing area
    /// (<see cref="TimeProviderNames.Grains"/>), which continues to resolve the default (unkeyed) provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is intended for tests which install a controllable clock as the default <see cref="TimeProvider"/> to
    /// drive grain timers deterministically, but need the silo''s background maintenance timers to remain on real
    /// time so that advancing the clock does not resume those loops inline.
    /// </para>
    /// <para>
    /// Rather than enumerating every area key (which would couple this helper to keys owned by extension packages),
    /// this registers a catch-all <see cref="KeyedService.AnyKey"/> provider so every keyed area resolves the supplied
    /// <paramref name="timeProvider"/>, then re-pins <see cref="TimeProviderNames.Grains"/> to the default provider.
    /// An exact key match takes precedence over the catch-all registration.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="timeProvider">The time provider to use for background areas.</param>
    public static IServiceCollection UseTimeProviderForBackgroundAreas(this IServiceCollection services, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // Pin every keyed area to the supplied provider (typically real time)...
        services.AddKeyedSingleton(KeyedService.AnyKey, (sp, _) => timeProvider);

        // ...except the grain-facing area, which keeps resolving the default (unkeyed) provider so that grain timers
        // and grain-side delays continue to observe the test clock.
        services.AddKeyedSingleton(TimeProviderNames.Grains, static (sp, _) => sp.GetRequiredService<TimeProvider>());

        return services;
    }
}
