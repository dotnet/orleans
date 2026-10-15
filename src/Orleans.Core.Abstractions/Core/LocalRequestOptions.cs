using System;
using System.Diagnostics.CodeAnalysis;
using Orleans.Concurrency;

namespace Orleans.Runtime;

/// <summary>
/// Options for a request which a grain queues on its own activation, using
/// <see cref="IGrainContext.ReceiveLocalRequest"/> or <see cref="GrainBaseExtensions"/>'s <c>RunRequestAsync</c> methods.
/// </summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Per-call options are passed to the runtime rather than compared.")]
public readonly struct LocalRequestOptions
{
    private readonly bool _suppressKeepAlive;

    /// <summary>
    /// Initializes a new <see cref="LocalRequestOptions"/> instance with the default options.
    /// </summary>
    public LocalRequestOptions()
    {
    }

    /// <summary>
    /// Gets a value indicating whether the request extends the lifetime of the grain activation.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// A request which keeps the activation alive restarts the activation's idle collection period when it completes,
    /// the same as a grain call. A request which does not keep the activation alive leaves the idle collection period unchanged.
    /// </remarks>
    public bool KeepAlive
    {
        get => !_suppressKeepAlive;
        init => _suppressKeepAlive = !value;
    }

    /// <summary>
    /// Gets a value indicating whether the request can interleave with other requests on the activation.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// If this value is <see langword="false"/>, the request follows the grain's reentrancy rules, the same as a grain call.
    /// If this value is <see langword="true"/>, the request interleaves with other requests, the same as a call to a method with
    /// the <see cref="AlwaysInterleaveAttribute"/> applied.
    /// </remarks>
    public bool Interleave { get; init; }
}
