namespace Orleans.Runtime.Utilities;

/// <summary>Defines key equality and a consistent unsigned hash-ring coordinate.</summary>
/// <remarks>
/// Equal keys have equal hashes. Keys retain their hash and equality identity while stored.
/// Implementations support concurrent calls and keep hashing and equality independent of collection mutations.
/// Hashes shared between silos are stable across processes.
/// </remarks>
internal interface IConsistentHashComparer<in T>
{
    bool Equals(T? x, T? y);

    uint GetHashCode(T value);
}
