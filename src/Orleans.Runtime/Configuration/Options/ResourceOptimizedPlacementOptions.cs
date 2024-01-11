using System;
using Microsoft.Extensions.Options;

namespace Orleans.Runtime.Configuration.Options;

/// <summary>
/// Settings which regulate the placement of grains across a cluster when using <see cref="ResourceOptimizedPlacement"/>.
/// </summary>
public sealed class ResourceOptimizedPlacementOptions
{
    /// <summary>
    /// The importance of the CPU usage by the silo.
    /// </summary>
    /// <remarks><i>
    /// <para>A <u>higher</u> value results in the placement favoring silos with <u>lower</u> cpu usage.</para>
    /// <para>Valid range is [0.00-1.00]</para>
    /// </i></remarks>
    public float CpuUsageWeight { get; set; } = DEFAULT_CPU_USAGE_WEIGHT;
    /// <summary>
    /// The default value of <see cref="CpuUsageWeight"/>.
    /// </summary>
    public const float DEFAULT_CPU_USAGE_WEIGHT = 0.4f;

    /// <summary>
    /// The importance of the memory usage by the silo.
    /// </summary>
    /// <remarks><i>
    /// <para>A <u>higher</u> value results in the placement favoring silos with <u>lower</u> memory usage.</para>
    /// <para>Valid range is [0.00-1.00]</para>
    /// </i></remarks>
    public float MemoryUsageWeight { get; set; } = DEFAULT_MEMORY_USAGE_WEIGHT;
    /// <summary>
    /// The default value of <see cref="MemoryUsageWeight"/>.
    /// </summary>
    public const float DEFAULT_MEMORY_USAGE_WEIGHT = 0.3f;

    /// <summary>
    /// The importance of the available memory to the silo.
    /// </summary>
    /// <remarks><i>
    /// <para>A <u>higher</u> values results in the placement favoring silos with <u>higher</u> available memory.</para>
    /// <para>Valid range is [0.00-1.00]</para>
    /// </i></remarks>
    public float AvailableMemoryWeight { get; set; } = DEFAULT_AVAILABLE_MEMORY_WEIGHT;
    /// <summary>
    /// The default value of <see cref="AvailableMemoryWeight"/>.
    /// </summary>
    public const float DEFAULT_AVAILABLE_MEMORY_WEIGHT = 0.2f;

    /// <summary>
    /// The importance of the physical memory to the silo.
    /// </summary>
    /// <remarks><i>
    /// <para>A <u>higher</u> values results in the placement favoring silos with <u>higher</u> physical memory.</para>
    /// <para>This may have an impact in clusters with resources distributed unevenly across silos.</para>
    /// <para>Valid range is [0.00-1.00]</para>
    /// </i></remarks>
    public float PhysicalMemoryWeight { get; set; } = DEFAULT_PHYSICAL_MEMORY_WEIGHT;
    /// <summary>
    /// The default value of <see cref="PhysicalMemoryWeight"/>.
    /// </summary>
    public const float DEFAULT_PHYSICAL_MEMORY_WEIGHT = 0.1f;

    /// <summary>
    /// The specified margin for which: if two silos (one of them being the local to the current pending activation), have a utilization score that should be considered "the same" within this margin.
    /// <list type="bullet">
    /// <item>When this value is 0, then the policy will always favor the silo with the lower resource utilization, even if that silo is remote to the current pending activation.</item>
    /// <item>When this value is 100, then the policy will always favor the local silo, regardless of its relative utilization score. This policy essentially becomes equivalent to <see cref="PreferLocalPlacement"/>.</item>
    /// </list>
    /// </summary>
    /// <remarks><i>
    /// <para>Do favor a lower value for this e.g: 1-5 [%]</para>
    /// <para>Valid range is [0.00-1.00]</para>
    /// </i></remarks>
    public float LocalSiloPreferenceMargin { get; set; }
    /// <summary>
    /// The default value of <see cref="LocalSiloPreferenceMargin"/>.
    /// </summary>
    public const float DEFAULT_LOCAL_SILO_PREFERENCE_MARGIN = 0.05f;
}

internal sealed class ResourceOptimizedPlacementOptionsValidator
    (IOptions<ResourceOptimizedPlacementOptions> options) : IConfigurationValidator
{
    private readonly ResourceOptimizedPlacementOptions _options = options.Value;

    public void ValidateConfiguration()
    {
        if (_options.CpuUsageWeight < 0f || _options.CpuUsageWeight > 1f)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.CpuUsageWeight));
        }

        if (_options.MemoryUsageWeight < 0f || _options.MemoryUsageWeight > 1f)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.MemoryUsageWeight));
        }

        if (_options.AvailableMemoryWeight < 0f || _options.AvailableMemoryWeight > 1f)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.AvailableMemoryWeight));
        }

        if (_options.PhysicalMemoryWeight < 0f || _options.PhysicalMemoryWeight > 1f)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.PhysicalMemoryWeight));
        }

        if (_options.LocalSiloPreferenceMargin < 0f || _options.LocalSiloPreferenceMargin > 1f)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.LocalSiloPreferenceMargin));
        }

        if (Truncate(_options.CpuUsageWeight) +
            Truncate(_options.MemoryUsageWeight) +
            Truncate(_options.AvailableMemoryWeight +
            Truncate(_options.PhysicalMemoryWeight)) != 1)
        {
            throw new OrleansConfigurationException($"The total sum across all the weights of {nameof(ResourceOptimizedPlacementOptions)} must equal 1");
        }

        static void ThrowOutOfRange(string propertyName)
            => throw new OrleansConfigurationException($"{propertyName} must be inclusive between [0-1]");

        static double Truncate(double value)
            => Math.Floor(value * 100) / 100;
    }
}