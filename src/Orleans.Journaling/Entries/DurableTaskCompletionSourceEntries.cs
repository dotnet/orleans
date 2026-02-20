namespace Orleans.Journaling;

/// <summary>
/// Base type for all DurableTaskCompletionSource log entries.
/// </summary>
public abstract record DurableTaskCompletionSourceEntry<T>;

/// <summary>
/// The task completed successfully with a value.
/// </summary>
public sealed record TcsCompletedEntry<T>(T Value) : DurableTaskCompletionSourceEntry<T>;

/// <summary>
/// The task faulted with an exception.
/// </summary>
public sealed record TcsFaultedEntry<T>(Exception Exception) : DurableTaskCompletionSourceEntry<T>;

/// <summary>
/// The task was canceled.
/// </summary>
public sealed record TcsCanceledEntry<T>() : DurableTaskCompletionSourceEntry<T>;

/// <summary>
/// The task is pending (no result yet).
/// </summary>
public sealed record TcsPendingEntry<T>() : DurableTaskCompletionSourceEntry<T>;
