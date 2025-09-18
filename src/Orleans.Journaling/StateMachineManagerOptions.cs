namespace Orleans.Journaling;

public sealed class StateMachineManagerOptions
{
    /// <summary>
    /// Specifies the period of time to wait until the manager retires
    /// a <see cref="IDurableStateMachine"/> if its not registered in the manager anymore.
    /// </summary>
    /// <remarks>The act of retirement removes this state machine and its data is purged.</remarks>
    public TimeSpan RetirementGracePeriod { get; set; }

    /// <summary>
    /// The default value of <see cref="RetirementGracePeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_RETIREMENT_GRACE_PERIOD = TimeSpan.FromHours(1);
}
