namespace Orleans.Journaling;

/// <summary>
/// Establishes the final prerequisite immediately before journal capture or deletion.
/// </summary>
/// <remarks>
/// An owner admits at most one capture hook in <see cref="IJournaledStateManager.Hooks"/>.
/// Its before callback runs after all ordinary before callbacks. The work loop awaits it directly,
/// then captures state or starts deletion without a further asynchronous phase.
/// Its prerequisites cover changes staged while its own I/O awaited. After callbacks retain
/// normal list order. Features inspect and deduplicate this registration using the same hook list.
/// </remarks>
public interface IJournaledStateCaptureHook : IJournaledStateHook
{
}
