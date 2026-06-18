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
    /// Gets or sets the maximum number of concurrent dissemination sends.
    /// </summary>
    public int MaxConcurrentSends { get; set; } = 32;

    /// <summary>
    /// Gets or sets how long peer capability probe results are cached.
    /// </summary>
    public TimeSpan CapabilityCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets how long failed peers are backed off before retrying.
    /// </summary>
    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum serialized bytes in one dissemination batch.
    /// </summary>
    public int MaxBatchBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets the maximum number of items in one dissemination batch.
    /// </summary>
    public int MaxBatchItems { get; set; } = 64;

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
    /// The argument is the current participant count for the selected dissemination topology.
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
}

/// <summary>
/// Options for a dissemination topic.
/// </summary>
public sealed class DisseminationTopicOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether this topic is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of pending topic items.
    /// </summary>
    public int MaxPendingItemCount { get; set; } = 1024;

    /// <summary>
    /// Gets or sets the maximum delay for topic coalescing.
    /// </summary>
    public TimeSpan MaxCoalescingDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets how long a topic item remains useful.
    /// </summary>
    public TimeSpan StaleItemTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets a value indicating whether topic-specific fallback is enabled.
    /// </summary>
    public bool FallbackEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum serialized payload size for this topic.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;
}
