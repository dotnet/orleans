using System.Diagnostics;

namespace Orleans.Runtime;

/// <summary>
/// The lifecycle state of an <see cref="ActivationData"/>.
/// </summary>
/// <remarks>
/// <para>
/// The states form a totally ordered chain which is symmetric around <see cref="Active"/>. Each setup phase is paired with its mirror,
/// <c>mirror(s) = Destroyed - s</c>, the teardown phase which is eligible to run once that setup phase completed. Group checks are range comparisons:
/// states before <see cref="Active"/> set the activation up and states after it tear the activation down.
/// </para>
/// <para>
/// The states are byte values spaced around <see cref="Active"/> at the center of the byte range, so that the state fits in a byte and a new
/// pair of phases can be inserted at mirrored values without renumbering the other phases.
/// </para>
/// <para>
/// Two ordering dependencies fix the positions of the directory and migration phases. Migrated state is installed only once the activation
/// holds its directory registration, so <see cref="Registering"/> precedes <see cref="Rehydrating"/>. A migrating activation hands its
/// registration to the migration target, and unregisters only if that handoff fails, so <see cref="Dehydrating"/> precedes
/// <see cref="Unregistering"/>. Only an activation which completed activation migrates, so <see cref="Dehydrating"/> discards a migration
/// requested during setup.
/// </para>
/// <para>
/// An activation advances one state at a time. Teardown starts at the mirror of the last completed setup phase and runs every later
/// teardown phase. A deactivation request during setup is recorded intent rather than a state: it cancels setup, setup stops at its next
/// phase boundary, and the deactivation operation enters teardown. Forced disposal moves an activation from any earlier state directly to <see cref="Disposing"/>,
/// skipping the phases which have not run.
/// </para>
/// </remarks>
internal enum ActivationState : byte
{
    /// <summary>
    /// The activation context exists, but it has no grain instance.
    /// </summary>
    None = 0,

    /// <summary>
    /// The grain instance and its per-activation components are being created, and the previous registration is read from a migration context.
    /// Mirrored by <see cref="Disposing"/>.
    /// </summary>
    Constructing = 20,

    /// <summary>
    /// The activation is being registered in the grain directory.
    /// Mirrored by <see cref="Unregistering"/>.
    /// </summary>
    Registering = 40,

    /// <summary>
    /// Migration participants are restoring state from a migration context.
    /// Mirrored by <see cref="Dehydrating"/>.
    /// </summary>
    Rehydrating = 60,

    /// <summary>
    /// The grain lifecycle is starting.
    /// Mirrored by <see cref="Stopping"/>.
    /// </summary>
    Starting = 80,

    /// <summary>
    /// <see cref="IGrainBase.OnActivateAsync"/> is running.
    /// Mirrored by <see cref="Deactivating"/>.
    /// </summary>
    Activating = 100,

    /// <summary>
    /// The activation processes requests.
    /// </summary>
    Active = 127,

    /// <summary>
    /// <see cref="IGrainBase.OnDeactivateAsync"/> is running.
    /// </summary>
    Deactivating = 154,

    /// <summary>
    /// The grain lifecycle is stopping. Stopping stops only the lifecycle stages which started, so it also undoes a partial start.
    /// </summary>
    Stopping = 174,

    /// <summary>
    /// A migrating activation is dehydrating its state and sending it, with its queued requests, to the migration target.
    /// </summary>
    Dehydrating = 194,

    /// <summary>
    /// The activation is being removed from the grain directory, unless its registration was handed to a migration target.
    /// </summary>
    Unregistering = 214,

    /// <summary>
    /// The grain instance and activation services are being disposed. Disposing also undoes a partial construction.
    /// When the deactivation operation runs teardown, the activation then leaves the catalog and rejects or forwards its queued requests.
    /// </summary>
    Disposing = 234,

    /// <summary>
    /// The activation's resources are disposed. A deactivation operation which forced disposal preempted still leaves the catalog afterwards.
    /// </summary>
    Destroyed = 254,
}

/// <summary>
/// Range checks and transition rules for <see cref="ActivationState"/>.
/// </summary>
internal static class ActivationStateExtensions
{
    /// <summary>
    /// Gets whether the activation is setting up, before it becomes <see cref="ActivationState.Active"/>.
    /// </summary>
    public static bool IsSettingUp(this ActivationState state) => state < ActivationState.Active;

    /// <summary>
    /// Gets whether the activation is tearing down, after it was <see cref="ActivationState.Active"/> or setup stopped.
    /// </summary>
    public static bool IsTearingDown(this ActivationState state) => state > ActivationState.Active;

    /// <summary>
    /// Gets whether the activation has started disposing, so it no longer holds a usable grain instance.
    /// </summary>
    public static bool IsDisposingOrDestroyed(this ActivationState state) => state >= ActivationState.Disposing;

    /// <summary>
    /// Gets the state which is paired with <paramref name="state"/>.
    /// </summary>
    public static ActivationState GetMirror(this ActivationState state) => (ActivationState)(ActivationState.Destroyed - state);

    /// <summary>
    /// Gets the state which follows <paramref name="state"/> in the chain.
    /// </summary>
    public static ActivationState GetNext(this ActivationState state) => state switch
    {
        ActivationState.None => ActivationState.Constructing,
        ActivationState.Constructing => ActivationState.Registering,
        ActivationState.Registering => ActivationState.Rehydrating,
        ActivationState.Rehydrating => ActivationState.Starting,
        ActivationState.Starting => ActivationState.Activating,
        ActivationState.Activating => ActivationState.Active,
        ActivationState.Active => ActivationState.Deactivating,
        ActivationState.Deactivating => ActivationState.Stopping,
        ActivationState.Stopping => ActivationState.Dehydrating,
        ActivationState.Dehydrating => ActivationState.Unregistering,
        ActivationState.Unregistering => ActivationState.Disposing,
        ActivationState.Disposing => ActivationState.Destroyed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The state has no successor."),
    };

    /// <summary>
    /// Gets the teardown phase which starts teardown once <paramref name="lastCompleted"/> is the last completed setup phase.
    /// </summary>
    /// <remarks>
    /// <see cref="ActivationState.Disposing"/> undoes the activation context as well as construction, so an activation which never
    /// completed construction enters teardown there.
    /// </remarks>
    public static ActivationState GetTeardownEntry(this ActivationState lastCompleted)
    {
        Debug.Assert(lastCompleted.IsSettingUp());
        return lastCompleted <= ActivationState.Constructing ? ActivationState.Disposing : lastCompleted.GetMirror();
    }

    /// <summary>
    /// Gets the teardown phase which starts teardown when the setup phase <paramref name="failed"/> fails.
    /// </summary>
    /// <remarks>
    /// A failed phase leaves the previous phase as the last completed phase. <see cref="ActivationState.Stopping"/> stops only the
    /// lifecycle stages which started, so a failed <see cref="ActivationState.Starting"/> phase enters teardown there.
    /// </remarks>
    public static ActivationState GetFailureTeardownEntry(this ActivationState failed) => failed switch
    {
        ActivationState.Constructing or ActivationState.Registering => ActivationState.Disposing,
        ActivationState.Rehydrating => ActivationState.Unregistering,
        ActivationState.Starting or ActivationState.Activating => ActivationState.Stopping,
        _ => throw new ArgumentOutOfRangeException(nameof(failed), failed, "Only a setup phase can fail."),
    };

    /// <summary>
    /// Gets whether an activation may transition from <paramref name="from"/> to <paramref name="to"/> without being forced.
    /// </summary>
    /// <remarks>
    /// Forced disposal additionally moves an activation from any state before <see cref="ActivationState.Disposing"/> to <see cref="ActivationState.Disposing"/>.
    /// </remarks>
    public static bool IsValidTransition(ActivationState from, ActivationState to)
    {
        // Each phase advances to the next one, including Activating to Active and Active to Deactivating.
        if (from is not ActivationState.Destroyed && to == from.GetNext())
        {
            return true;
        }

        if (!from.IsSettingUp())
        {
            return false;
        }

        // Setup stops after a completed phase, or when a phase fails.
        if (to == from.GetTeardownEntry())
        {
            return true;
        }

        return from > ActivationState.None && to == from.GetFailureTeardownEntry();
    }
}
