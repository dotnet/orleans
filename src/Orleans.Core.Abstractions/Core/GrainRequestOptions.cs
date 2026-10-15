using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime;

/// <summary>
/// Options for requests which a grain runs on its own activation using
/// <see cref="GrainBaseExtensions.RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, GrainRequestOptions, CancellationToken)"/>.
/// </summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "These per-call options carry a caller-owned arguments array rather than representing a comparable value.")]
public readonly struct GrainRequestOptions
{
    private readonly bool _suppressKeepAlive;

    /// <summary>
    /// Initializes a new <see cref="GrainRequestOptions"/> instance with the default options.
    /// </summary>
    public GrainRequestOptions()
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

    /// <summary>
    /// Gets the interface name which grain call filters observe for the request.
    /// Defaults to <c>IGrainRequestInvoker</c>.
    /// </summary>
    /// <remarks>
    /// Call filters observe this value as <see cref="IGrainCallContext.InterfaceName"/>, and tracing uses it in the request's activity name.
    /// </remarks>
    public string? InterfaceName { get; init; }

    /// <summary>
    /// Gets the method name which grain call filters observe for the request.
    /// Defaults to <c>RunRequestAsync</c>.
    /// </summary>
    /// <remarks>
    /// Call filters observe this value as <see cref="IGrainCallContext.MethodName"/>, and tracing uses it in the request's activity name.
    /// </remarks>
    public string? MethodName { get; init; }

    /// <summary>
    /// Gets the arguments which grain call filters observe for the request.
    /// Defaults to no arguments.
    /// </summary>
    /// <remarks>
    /// The arguments describe the request to call filters, which access them through <see cref="IGrainCallContext.Request"/>.
    /// The callback receives its state rather than these arguments. A call filter which sets an argument writes to this array.
    /// </remarks>
    public object?[]? Arguments { get; init; }
}
