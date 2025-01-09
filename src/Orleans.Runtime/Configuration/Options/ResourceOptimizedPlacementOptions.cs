using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration;

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
    public const int DEFAULT_MEMORY_USAGE_WEIGHT = 20;

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
    /// The importance of the maximum available memory to the silo.
    /// </summary>
    /// <remarks><i>
    /// <para>A <u>higher</u> values results in the placement favoring silos with <u>higher</u> maximum available memory.</para>
    /// <para>This may have an impact in clusters with resources distributed unevenly across silos.</para>
    /// <para>This relates strongly to the physical memory in the silo, and the configured memory limit (if it has been configured).</para>
    /// <para>Valid range is [0-100]</para>
    /// </i></remarks>
    public int MaxAvailableMemoryWeight { get; set; } = DEFAULT_MAX_AVAILABLE_MEMORY_WEIGHT;

    /// <summary>
    /// The default value of <see cref="MaxAvailableMemoryWeight"/>.
    /// </summary>
    public const int DEFAULT_MAX_AVAILABLE_MEMORY_WEIGHT = 5;

    /// <summary>
    /// The importance of the current activation count to the silo.
    /// </summary>
    /// <remarks><i>
    /// <para>A <u>higher</u> values results in the placement favoring silos with <u>lower</u> activation count.</para>
    /// <para>Valid range is [0-100]</para>
    /// </i></remarks>
    public int ActivationCountWeight { get; set; } = DEFAULT_ACTIVATION_COUNT_WEIGHT;

    /// <summary>
    /// The default value of <see cref="ActivationCountWeight"/>.
    /// </summary>
    public const int DEFAULT_ACTIVATION_COUNT_WEIGHT = 15;

    /// <summary>
    /// The specified margin for which: if two silos (one of them being the local to the current pending activation), have a utilization score that should be considered "the same" within this margin.
    /// <list type="bullet">
    /// <item><description>When this value is 0, then the policy will always favor the silo with the lower resource utilization, even if that silo is remote to the current pending activation.</description></item>
    /// <item><description>When this value is 100, then the policy will always favor the local silo, regardless of its relative utilization score. This policy essentially becomes equivalent to <see cref="PreferLocalPlacement"/>.</description></item>
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

        if (_options.MaxAvailableMemoryWeight < 0 || _options.MaxAvailableMemoryWeight > 100)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.MaxAvailableMemoryWeight));
        }

        if (_options.ActivationCountWeight < 0 || _options.ActivationCountWeight > 100)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.ActivationCountWeight));
        }

        if (_options.LocalSiloPreferenceMargin < 0 || _options.LocalSiloPreferenceMargin > 100)
        {
            ThrowOutOfRange(nameof(ResourceOptimizedPlacementOptions.LocalSiloPreferenceMargin));
        }

        static void ThrowOutOfRange(string propertyName)
            => throw new OrleansConfigurationException($"{propertyName} must be inclusive between [0-100]");
    }
}