namespace Orleans.Journaling;

/// <summary>
/// Identifies a state.
/// </summary>
/// <param name="Value">The underlying identity value.</param>
public readonly record struct JournalStreamId(uint Value);
