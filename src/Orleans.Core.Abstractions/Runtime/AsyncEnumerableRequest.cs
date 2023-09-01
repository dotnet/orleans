using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Invocation;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// Identifies enumeration results.
/// </summary>
[GenerateSerializer]
public enum EnumerationResult
{
    /// <summary>
    /// This result represents a heartbeat. Issue a subsequent enumeration call to receive a new result.
    /// </summary>
    Heartbeat = 1,

    /// <summary>
    /// This result represents a value from the enumeration.
    /// </summary>
    Element = 1 << 1,

    /// <summary>
    /// This result represents a sequence of values from the enumeration.
    /// </summary>
    Batch = 1 << 2,

    /// <summary>
    /// This result indicates that enumeration has completed and that no further results will be produced.
    /// </summary>
    Completed = 1 << 3,

    /// <summary>
    /// The attempt to enumerate failed because the enumerator was not found.
    /// </summary>
    MissingEnumeratorError = 1 << 4,

    /// <summary>
    /// This result indicates that enumeration has completed and that no further results will be produced.
    /// </summary>
    CompletedWithElement = Completed | Element,

    /// <summary>
    /// This result indicates that enumeration has completed and that no further results will be produced.
    /// </summary>
    CompletedWithBatch = Completed | Batch,
}

/// <summary>
/// Grain extension interface for grains which return <see cref="IAsyncEnumerable{T}"/> from grain methods.
/// </summary>
public interface IAsyncEnumerableGrainExtension : IGrainExtension
{
    /// <summary>
    /// Invokes an <see cref="IAsyncEnumerable{T}"/> request and begins enumeration.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="requestId">The request id, generated by the caller.</param>
    /// <param name="request">The request.</param>
    /// <returns>The result of enumeration</returns>
    [AlwaysInterleave]
    public ValueTask<(EnumerationResult Status, object Value)> StartEnumeration<T>(Guid requestId, [Immutable] IAsyncEnumerableRequest<T> request);

    /// <summary>
    /// Continues enumerating an <see cref="IAsyncEnumerable{T}"/> value.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="requestId">The request id, generated by the caller.</param>
    /// <returns>The result of enumeration</returns>
    [AlwaysInterleave]
    public ValueTask<(EnumerationResult Status, object Value)> MoveNext<T>(Guid requestId);

    /// <summary>
    /// Disposes an <see cref="IAsyncEnumerable{T}"/> value.
    /// </summary>
    /// <param name="requestId">The request id, generated by the caller.</param>
    /// <returns>A task representing the operation.</returns>
    [AlwaysInterleave]
    public ValueTask DisposeAsync(Guid requestId);
}

/// <summary>
/// Interface for requests to a <see cref="IAsyncEnumerable{T}"/>-returning methods.
/// </summary>
public interface IAsyncEnumerableRequest<T> : IRequest
{
    /// <summary>
    /// Gets or sets the maximum batch size for the request.
    /// </summary>
    int MaxBatchSize { get; set; }

    /// <summary>
    /// Invokes the request.
    /// </summary>
    /// <returns>The result of invocation.</returns>
    IAsyncEnumerable<T> InvokeImplementation();
}

/// <summary>
/// Represents a request to an <see cref="IAsyncEnumerable{T}"/>-returning method.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
[GenerateSerializer]
[SuppressReferenceTracking]
[ReturnValueProxy(nameof(InitializeRequest))]
public abstract class AsyncEnumerableRequest<T> : RequestBase, IAsyncEnumerable<T>, IAsyncEnumerableRequest<T>
{
    /// <summary>
    /// The target grain instance.
    /// </summary>
    [field: NonSerialized]
    internal GrainReference TargetGrain { get; private set; }

    /// <inheritdoc/>
    [Id(0)]
    public int MaxBatchSize { get; set; } = 100;

    /// <inheritdoc/>
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new AsyncEnumeratorProxy<T>(this, cancellationToken);

    // Called upon creation in generated code by the creating grain reference by virtue of the [ReturnValueProxy(nameof(InitializeRequest))] attribute on this class.
    public IAsyncEnumerable<T> InitializeRequest(GrainReference targetGrainReference)
    {
        TargetGrain = targetGrainReference;
        return this;
    }

    /// <inheritdoc/>
    public override ValueTask<Response> Invoke() => throw new NotImplementedException($"{nameof(IAsyncEnumerable<T>)} requests can not be invoked directly");

    /// <inheritdoc/>
    public IAsyncEnumerable<T> InvokeImplementation() => InvokeInner();

    // Generated
    protected abstract IAsyncEnumerable<T> InvokeInner();
}

/// <summary>
/// A proxy for an <see cref="IAsyncEnumerator{T}"/> instance returned from a grain method.
/// </summary>
internal sealed class AsyncEnumeratorProxy<T> : IAsyncEnumerator<T>
{
    private readonly AsyncEnumerableRequest<T> _request;
    private readonly CancellationToken _cancellationToken;
    private readonly IAsyncEnumerableGrainExtension _target;
    private readonly Guid _requestId;
    private (EnumerationResult State, object Value) _current;
    private int _batchIndex;
    private bool _disposed;
    private bool _initialized;

    private bool IsBatch => (_current.State & EnumerationResult.Batch) != 0;
    private bool IsElement => (_current.State & EnumerationResult.Element) != 0;
    private bool IsCompleted => (_current.State & EnumerationResult.Completed) != 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncEnumeratorProxy{T}"/> class.
    /// </summary>
    /// <param name="request">The request which this instanced proxies.</param>
    public AsyncEnumeratorProxy(AsyncEnumerableRequest<T> request, CancellationToken cancellationToken)
    {
        _request = request;
        _cancellationToken = cancellationToken;
        _requestId = Guid.NewGuid();
        _target = _request.TargetGrain.AsReference<IAsyncEnumerableGrainExtension>();
    }

    public int MaxBatchSize { get; set; } = 100;

    /// <inheritdoc/>
    public T Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsElement)
            {
                return (T)_current.Value;
            }

            if (IsBatch)
            {
                return ((List<T>)_current.Value)[_batchIndex];
            }

            throw new InvalidOperationException("Cannot get current value of an invalid enumerator.");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_initialized)
        {
            try
            {
                await _target.DisposeAsync(_requestId);
            }
            catch (Exception exception)
            {
                var logger = ((GrainReference)_target).Shared.ServiceProvider.GetRequiredService<ILogger<AsyncEnumerableRequest<T>>>();
                logger.LogWarning(exception, "Failed to dispose async enumerator.");
            }
        }

        _disposed = true;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> MoveNextAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Enumerate the existing batch before fetching more.
        if (IsBatch && ++_batchIndex < ((List<T>)_current.Value).Count)
        {
            return true;
        }

        if (IsCompleted)
        {
            return false;
        }

        (EnumerationResult Status, object Value) result;
        while (true)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _current = default;
                return false;
            }

            if (!_initialized)
            {
                result = await _target.StartEnumeration(_requestId, _request);
                _initialized = true;
            }
            else
            {
                result = await _target.MoveNext<T>(_requestId);
            }

            if (result.Status is not EnumerationResult.Heartbeat)
            {
                break;
            }
        }

        if (result.Status is EnumerationResult.MissingEnumeratorError)
        {
            throw new EnumerationAbortedException("Enumeration aborted: the remote target does not have a record of this enumerator."
                + " This likely indicates that the remote grain was deactivated since enumeration begun.");
        }

        Debug.Assert((result.Status & (EnumerationResult.Element | EnumerationResult.Batch | EnumerationResult.Completed)) != 0);

        _batchIndex = 0;
        _current = result;
        return (result.Status & (EnumerationResult.Element | EnumerationResult.Batch)) != 0;
    }
}

public static class AsyncEnumerableExtensions
{
    public static IAsyncEnumerable<T> WithBatchSize<T>(this IAsyncEnumerable<T> self, int maxBatchSize)
    {
        if (self is AsyncEnumerableRequest<T> request)
        {
            request.MaxBatchSize = maxBatchSize;
            return request;
        }

        return self;
    }
}

/// <summary>
/// Indicates that an enumeration was aborted.
/// </summary>
[GenerateSerializer]
public sealed class EnumerationAbortedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EnumerationAbortedException"/> class.
    /// </summary>
    public EnumerationAbortedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EnumerationAbortedException"/> class.
    /// </summary>
    public EnumerationAbortedException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EnumerationAbortedException"/> class.
    /// </summary>
    public EnumerationAbortedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EnumerationAbortedException"/> class.
    /// </summary>
    protected EnumerationAbortedException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}
