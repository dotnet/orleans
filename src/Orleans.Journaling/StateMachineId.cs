namespace Orleans.Journaling;

/// <summary>
/// Identifies a state machine.
/// </summary>
/// <param name="Value">The underlying identity value.</param>
public readonly record struct StateMachineId(ulong Value);
