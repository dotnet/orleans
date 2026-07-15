using System;

namespace Orleans.Configuration;

/// <summary>
/// Options for configuring internal silo-to-silo dissemination.
/// </summary>
public sealed class DisseminationOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the dissemination subsystem is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent dissemination broadcast sends.
    /// </summary>
    public int MaxConcurrentSends { get; set; } = 32;

    /// <summary>
    /// Gets or sets the maximum total payload bytes in one dissemination batch.
    /// </summary>
    public int MaxBatchBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum number of items in one dissemination batch.
    /// </summary>
    public int MaxBatchItems { get; set; } = 8 * 1024;

    /// <summary>
    /// Gets or sets overlay-specific dissemination options.
    /// </summary>
    public DisseminationOverlayOptions Overlay { get; set; } = new();
}

/// <summary>
/// Options for the dissemination overlay.
/// </summary>
public sealed class DisseminationOverlayOptions
{
    /// <summary>
    /// Gets or sets the code-configured fanout selector.
    /// </summary>
    /// <remarks>
    /// The argument is the current member count for the selected dissemination topology.
    /// When this value is <see langword="null"/>, <see cref="TargetHopCount"/>, <see cref="MinFanOutFactor"/>,
    /// and <see cref="MaxFanOutFactor"/> are used to derive a fanout factor.
    /// </remarks>
    public Func<int, int>? FanOutFactor { get; set; }

    /// <summary>
    /// Gets or sets the target number of tree hops used by the bindable fanout selector.
    /// </summary>
    public int TargetHopCount { get; set; } = 2;

    /// <summary>
    /// Gets or sets the minimum fanout factor used by the bindable fanout selector.
    /// </summary>
    public int MinFanOutFactor { get; set; } = 4;

    /// <summary>
    /// Gets or sets the maximum fanout factor used by the bindable fanout selector.
    /// </summary>
    public int MaxFanOutFactor { get; set; } = 32;

    /// <summary>
    /// Gets or sets the interval between anti-entropy repair rounds.
    /// </summary>
    public TimeSpan AntiEntropyInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the number of peers contacted during each anti-entropy repair round.
    /// </summary>
    public int AntiEntropyPeerCount { get; set; } = 3;

    internal int GetFanOutFactor(int memberCount)
    {
        var count = Math.Max(1, memberCount);
        var selectedFanOut = FanOutFactor?.Invoke(count) ?? GetConfiguredFanOutFactor(count);
        return Math.Clamp(selectedFanOut, 1, count);
    }

    internal int GetConfiguredFanOutFactor(int memberCount)
    {
        var count = Math.Max(1, memberCount);
        var targetHopCount = Math.Max(1, TargetHopCount);
        var scaled = targetHopCount switch
        {
            1 => count,
            2 => Math.Sqrt(count),
            3 => Math.Cbrt(count),
            _ => Math.Pow(count, 1d / targetHopCount),
        };
        var min = Math.Max(1, MinFanOutFactor);
        var max = Math.Max(min, MaxFanOutFactor);
        return (int)Math.Ceiling(Math.Max(min, Math.Min(scaled, max)));
    }
}

/// <summary>
/// Options for a dissemination namespace.
/// </summary>
public sealed class DisseminationNamespaceOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether this namespace is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the dissemination priority for this namespace.
    /// </summary>
    /// <remarks>
    /// <see cref="DisseminationPriority.High"/> namespaces bypass the coalescing window and are placed ahead of
    /// lower-priority namespaces within each per-peer batch, so their updates are disseminated as quickly as
    /// possible. <see cref="MaxCoalescingDelay"/> is not applied to high-priority namespaces.
    /// </remarks>
    public DisseminationPriority Priority { get; set; } = DisseminationPriority.Normal;

    /// <summary>
    /// Gets or sets the maximum number of pending namespace keys per peer.
    /// </summary>
    public int MaxPendingItemCount { get; set; } = 1024;

    /// <summary>
    /// Gets or sets the maximum delay for namespace coalescing.
    /// </summary>
    /// <remarks>
    /// Per-peer batches can contain values from multiple namespaces and use the shortest configured delay among
    /// enabled namespaces. High-priority namespaces (see <see cref="Priority"/>) do not coalesce and are excluded
    /// from this calculation.
    /// </remarks>
    public TimeSpan MaxCoalescingDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets how long a namespace value remains useful.
    /// </summary>
    public TimeSpan StaleItemTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the expected cadence for updates in this namespace.
    /// </summary>
    /// <remarks>
    /// Anti-entropy requests omit keys whose version advanced within this interval. Duplicate values do not
    /// postpone repair probes.
    /// </remarks>
    public TimeSpan ExpectedUpdateCadence { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the maximum serialized payload size for this namespace.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;
}

/// <summary>
/// Describes how urgently a dissemination namespace's updates are broadcast relative to other namespaces.
/// </summary>
public enum DisseminationPriority
{
    /// <summary>
    /// Updates are coalesced within the namespace's <see cref="DisseminationNamespaceOptions.MaxCoalescingDelay"/>
    /// and are sent after any higher-priority namespaces.
    /// </summary>
    Normal,

    /// <summary>
    /// Updates bypass the coalescing window, are sent immediately, and are placed ahead of normal-priority
    /// namespaces within each per-peer batch.
    /// </summary>
    High,
}
