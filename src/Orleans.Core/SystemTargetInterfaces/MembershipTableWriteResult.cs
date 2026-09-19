using System;
using Orleans.Concurrency;

namespace Orleans;

/// <summary>
/// Describes the outcome of a conditional membership mutation and its optional commit metadata.
/// </summary>
[GenerateSerializer, Immutable]
public readonly struct MembershipTableWriteResult
{
    /// <summary>
    /// Initializes a membership mutation result.
    /// </summary>
    /// <param name="succeeded">Whether the mutation succeeded.</param>
    /// <param name="receipt">Metadata from the successful commit, when supplied by the provider.</param>
    /// <exception cref="ArgumentException">A receipt is supplied for an unsuccessful mutation.</exception>
    public MembershipTableWriteResult(bool succeeded, MembershipTableWriteReceipt? receipt = null)
    {
        if (!succeeded && receipt is not null)
        {
            throw new ArgumentException("A commit receipt requires a successful mutation.", nameof(receipt));
        }

        Succeeded = succeeded;
        Receipt = receipt;
    }

    /// <summary>
    /// Gets whether the conditional mutation succeeded.
    /// </summary>
    [Id(0)]
    public bool Succeeded { get; }

    /// <summary>
    /// Gets metadata from this mutation's commit, or <see langword="null"/> when the provider
    /// supplied only the mutation's success status.
    /// </summary>
    [Id(1)]
    public MembershipTableWriteReceipt? Receipt { get; }
}

/// <summary>
/// Contains the table version and row entity tag produced by one committed membership mutation.
/// </summary>
/// <remarks>
/// These values describe the originating commit. Subsequent activity can change the stored metadata.
/// Row entity tags follow the provider's metadata and heartbeat-neutral concurrency policy.
/// </remarks>
[GenerateSerializer, Immutable]
public sealed class MembershipTableWriteReceipt
{
    /// <summary>
    /// Initializes metadata for a committed membership mutation.
    /// </summary>
    /// <param name="version">The table version and entity tag produced by the commit.</param>
    /// <param name="rowETag">The written row's provider-defined entity tag at the commit.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MembershipTableWriteReceipt(TableVersion version, string rowETag)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(rowETag);
        Version = version;
        RowETag = rowETag;
    }

    /// <summary>
    /// Gets the table version and entity tag produced by the commit.
    /// </summary>
    [Id(0)]
    public TableVersion Version { get; }

    /// <summary>
    /// Gets the written row's provider-defined entity tag at the commit.
    /// </summary>
    [Id(1)]
    public string RowETag { get; }
}
