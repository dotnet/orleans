using Microsoft.Extensions.Options;

namespace Orleans.Runtime.Configuration.Options;

/// <summary>
/// Settings which regulate the placement of grains across a cluster when using <see cref="ResourceOptimizedPlacement"/>.
/// </summary>
/// <remarks><i>All 'weight' properties, are relative to each other.</i></remarks>
public sealed class ResourceOptimizedPlacementOptions
{
    /// <summary>
    /// The importance of the CPU usage by the silo.
    /// </summary>
    /// <remarks><i>
    /// <para>A <u>higher</u> value results in the placement favoring silos with <u>lower</u> cpu usage.</para>
    /// <para>Valid range is [0-100]</para>
    /// </i></remarks>
    public int CpuUsageWeight { get; set; } = DEFAULT_CPU_USAGE_WEIGHT;

    /// <summary>
    /// The default value of <see cref="CpuUsageWeight"/>.
    /// </summary>
    public const int DEFAULT_CPU_USAGE_WEIGHT = 40;

    /// <summary>
    /// The importance of the memory usage by the silo.
    /// </summary>
    /// <remarks><i>
    /// <para>A <u>higher</u> value results in the placement favoring silos with <u>lower</u> memory usage.</para>
    /// <para>Valid range is [0-100]</para>
    /// </i></remarks>
    public int MemoryUsageWeight { get; set; } = DEFAULT_MEMORY_USAGE_WEIGHT;

    /// <summary>
    /// The default value of <see cref="MemoryUsageWeight"/>.
    /// </summary>
    public const int DEFAULT_MEMORY_USAGE_WEIGHT = 30;

    /// <summary>
    /// The importance of the available memory to the silo.
    /// </summary>
    /// <remarks><i>
    /// <para>A <u>higher</u> values results in the placement favoring silos with <u>higher</u> available memory.</para>
    /// <para>Valid range is [0-100]</para>
    /// </i></remarks>
    public int AvailableMemoryWeight { get; set; } = DEFAULT_AVAILABLE_MEMORY_WEIGHT;

    /// <summary>
    /// The default value of <see cref="AvailableMemoryWeight"/>.
    /// </summary>
    public const int DEFAULT_AVAILABLE_MEMORY_WEIGHT = 20;

    /// <summary>
    /// The importance of the physical memory to the silo.
    /// </summary>
    /// <remarks><i>
    /// <para>A <u>higher</u> values results in the placement favoring silos with <u>higher</u> physical memory.</para>
    /// <para>This may have an impact in clusters with resources distributed unevenly across silos.</para>
    /// <para>Valid range is [0-100]</para>
    /// </i></remarks>
    public int PhysicalMemoryWeight { get; set; } = DEFAULT_PHYSICAL_MEMORY_WEIGHT;

    /// <summary>
    /// The default value of <see cref="PhysicalMemoryWeight"/>.
    /// </summary>
    public const int DEFAULT_PHYSICAL_MEMORY_WEIGHT = 10;

    /// <summary>
    /// The specified margin for which: if two silos (one of them being the local to the current pending activation), have a utilization score that should be considered "the same" within this margin.
    /// <list type="bullet">
    /// <item>When this value is 0, then the policy will always favor the silo with the lower resource utilization, even if that silo is remote to the current pending activation.</item>
    /// <item>When this value is 100, then the policy will always favor the local silo, regardless of its relative utilization score. This policy essentially becomes equivalent to <see cref="PreferLocalPlacement"/>.</item>
    /// </list>
    /// </summary>
    /// <remarks><i>
    /// <para>Do favor a lower value for this e.g: 5-10</para>
    /// <para>Valid range is [0-100]</para>
    /// </i></remarks>
    public int LocalSiloPreferenceMargin { get; set; } = DEFAULT_LOCAL_SILO_PREFERENCE_MARGIN;

    /// <summary>
    /// The default value of <see cref="LocalSiloPreferenceMargin"/>.
    /// </summary>
    public const int DEFAULT_LOCAL_SILO_PREFERENCE_MARGIN = 5;
}

internal sealed class ResourceOptimizedPlacementOptionsValidator
    (IOptions<ResourceOptimizedPlacementOptions> options) : IConfigurationValidator
{
    private readonly ResourceOptimizedPlacementOptions _options = options.Value;

    public void ValidateConfiguration()
    {
        if (_options.CpuUsageWeight < 0 || _options.CpuUsageWeight > 100)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.CpuUsageWeight));
        }

        if (_options.MemoryUsageWeight < 0 || _options.MemoryUsageWeight > 100)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.MemoryUsageWeight));
        }

        if (_options.AvailableMemoryWeight < 0 || _options.AvailableMemoryWeight > 100)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.AvailableMemoryWeight));
        }

        if (_options.PhysicalMemoryWeight < 0 || _options.PhysicalMemoryWeight > 100)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.PhysicalMemoryWeight));
        }

        if (_options.LocalSiloPreferenceMargin < 0 || _options.LocalSiloPreferenceMargin > 100)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.LocalSiloPreferenceMargin));
        }

        static void ThrowOutOfRange(string propertyName)
            => throw new OrleansConfigurationException($"{propertyName} must be inclusive between [0-100]");
    }
}