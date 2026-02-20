namespace Orleans.Journaling;

/// <summary>
/// Base type for all DurableValue log entries.
/// </summary>
public abstract record DurableValueEntry<T>;

/// <summary>
/// Sets the value.
/// </summary>
public sealed record ValueSetEntry<T>(T Value) : DurableValueEntry<T>;
