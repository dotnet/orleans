using Orleans.Journaling.Json;

namespace Orleans.Journaling;

/// <summary>
/// Options to configure the <see cref="IJournaledStateManager"/>.
/// </summary>
public sealed class JournaledStateManagerOptions
{
    /// <summary>
    /// Gets or sets the journal format key used to write journal data.
    /// </summary>
    public string JournalFormatKey { get; set; } = JsonJournalExtensions.JournalFormatKey;

    /// <summary>
    /// Specifies the period of time to wait until the manager retires
    /// a <see cref="IJournaledState"/> if it's not registered in the manager anymore.
    /// </summary>
    /// <remarks>
    /// <para>The act of retirement removes this state from the journal.</para>
    /// <para>If the state is reintroduced (within the grace period), then it will not be removed by the manager.</para>
    /// <para>
    /// This value represents the <b>minimum</b> time the fate of the state will be postponed.
    /// The final decision can take longer - usually <see cref="RetirementGracePeriod"/> + [time until next compaction occurs].
    /// </para>
    /// </remarks>
    public TimeSpan RetirementGracePeriod { get; set; } = DEFAULT_RETIREMENT_GRACE_PERIOD;

    /// <summary>
    /// The default value of <see cref="RetirementGracePeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_RETIREMENT_GRACE_PERIOD = TimeSpan.FromDays(7);
}
