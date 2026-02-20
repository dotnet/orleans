namespace Orleans.Journaling;

/// <summary>
/// Base type for all DurableState log entries.
/// </summary>
public abstract record DurableStateEntry<T>;

/// <summary>
/// Sets the state value with a version number.
/// </summary>
public sealed record StateSetEntry<T>(T State, ulong Version) : DurableStateEntry<T>;

/// <summary>
/// Clears the state value.
/// </summary>
public sealed record StateClearEntry<T>() : DurableStateEntry<T>;
