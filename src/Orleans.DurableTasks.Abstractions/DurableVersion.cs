namespace Orleans.DurableTasks;

/// <summary>
/// Describes a change to durable task code whose versions coexist while executions which started on earlier code are
/// pending: its name, and the versions which the running code supports.
/// </summary>
/// <remarks>
/// <para>
/// Declare each change once, typically as a static field, and read the running execution's version with
/// <see cref="Get"/> wherever the code branches on the change:
/// <c>static readonly DurableVersion CarrierApi = new("carrier-api", minSupported: 0, maxSupported: 1);</c>. Every check
/// then states the same range, so a deployment which adds a version or retires one edits the declaration.
/// </para>
/// <para>
/// <see cref="Get"/> returns what <see cref="DurableTask.GetVersion(string, int, int)"/> returns for the declared name
/// and range.
/// </para>
/// </remarks>
public sealed class DurableVersion
{
    /// <summary>Initializes a description of the change <paramref name="name"/>.</summary>
    /// <param name="name">The change's name, unique among the changes which one task checks. Names don't contain <c>$</c>.</param>
    /// <param name="minSupported">
    /// The oldest version which the code supports. Version <c>0</c> is the code before the change's first version.
    /// </param>
    /// <param name="maxSupported">The newest version, which executions that reach a check for the first time record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains <c>$</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minSupported"/> is negative, or <paramref name="maxSupported"/> is less than
    /// <paramref name="minSupported"/>.
    /// </exception>
    public DurableVersion(string name, int minSupported, int maxSupported)
    {
        DurableTaskContext.ValidateName(name, nameof(name));
        ArgumentOutOfRangeException.ThrowIfNegative(minSupported);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSupported, minSupported);
        Name = name;
        MinSupported = minSupported;
        MaxSupported = maxSupported;
    }

    /// <summary>Gets the change's name.</summary>
    public string Name { get; }

    /// <summary>Gets the oldest version which the code supports.</summary>
    public int MinSupported { get; }

    /// <summary>Gets the newest version, which executions that reach a check for the first time record.</summary>
    public int MaxSupported { get; }

    /// <summary>Returns the version of this change which the running durable task follows.</summary>
    /// <returns>A version from <see cref="MinSupported"/> to <see cref="MaxSupported"/>.</returns>
    /// <remarks>
    /// The first check in an execution decides and records the version, as
    /// <see cref="DurableTask.GetVersion(string, int, int)"/> describes. Outside a durable task, the check returns
    /// <see cref="MaxSupported"/>.
    /// </remarks>
    /// <exception cref="DurableTaskAttemptCanceledException">
    /// The host canceled the attempt, for example because the execution recorded a version outside the supported range,
    /// which stalls it; the exception's inner exception is then a <see cref="DurableTaskVersionException"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The host can't record the version from the calling code, for example after <c>ConfigureAwait(false)</c>.
    /// </exception>
    public int Get() => DurableTaskContext.Ambient is { } context
        ? context.GetVersion(Name, MinSupported, MaxSupported)
        : MaxSupported;

    /// <summary>Returns whether the running durable task follows <paramref name="version"/> of this change or a newer one.</summary>
    /// <param name="version">The version which introduced the behavior which the caller selects.</param>
    /// <returns><see langword="true"/> when <see cref="Get"/> returns <paramref name="version"/> or more.</returns>
    /// <exception cref="DurableTaskAttemptCanceledException">
    /// The host canceled the attempt, for example because the execution recorded a version outside the supported range,
    /// which stalls it; the exception's inner exception is then a <see cref="DurableTaskVersionException"/>.
    /// </exception>
    public bool IsAtLeast(int version) => Get() >= version;

    /// <inheritdoc />
    public override string ToString() => $"{Name} [{MinSupported}..{MaxSupported}]";
}
