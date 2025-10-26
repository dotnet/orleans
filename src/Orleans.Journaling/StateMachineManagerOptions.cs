namespace Orleans.Journaling;

/// <summary>
/// Options to configure the <see cref="IStateMachineManager"/>.
/// </summary>
public sealed class StateMachineManagerOptions
{
    /// <summary>
    /// Specifies the period of time to wait until the manager retires
    /// a <see cref="IDurableStateMachine"/> if its not registered in the manager anymore.
    /// </summary>
    /// <remarks>
    /// <para>The act of retirement removes this state machine from the log.</para>
    /// <para>If the state machine is reintroduced (within the grace period), than it will not be removed by the manager.</para>
    /// <para>
    /// This value represents the <b>minimum</b> time the fate of the state machine will be postponed.
    /// The final decision can take longer - usually <see cref="RetirementGracePeriod"/> + [time until next compaction occurs].
    /// </para>
    /// </remarks>
    public TimeSpan RetirementGracePeriod { get; set; } = DEFAULT_RETIREMENT_GRACE_PERIOD;

    /// <summary>
    /// The default value of <see cref="RetirementGracePeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_RETIREMENT_GRACE_PERIOD = TimeSpan.FromDays(7);
}
