using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// An analogue to <see cref="CancellationToken"/> which can be sent between grains.
    /// </summary>
    [Immutable]
    public sealed class GrainCancellationToken : IDisposable
    {
        /// <summary>
        /// The underlying cancellation token source.
        /// </summary>
        [NonSerialized]
        private readonly CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// References to remote grains to which this token was passed.
        /// </summary>
        [NonSerialized]
        private readonly ConcurrentDictionary<GrainId, GrainReference> _targetGrainReferences;

        /// <summary>
        /// The runtime used to manage grain cancellation tokens.
        /// </summary>
        [NonSerialized]
        private IGrainCancellationTokenRuntime _cancellationTokenRuntime;

        /// <summary>
        /// Initializes the <see cref="T:Orleans.GrainCancellationToken"/>.
        /// </summary>
        /// <param name="id">
        /// The token id.
        /// </param>
        internal GrainCancellationToken(Guid id)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            Id = id;
            _targetGrainReferences = new ConcurrentDictionary<GrainId, GrainReference>();
        }


        /// <summary>
        /// Initializes the <see cref="T:Orleans.GrainCancellationToken"/>.
        /// </summary>
        /// <param name="id">
        /// The token id.
        /// </param>
        /// <param name="canceled">
        /// Whether or not the instance is already canceled.
        /// </param>
        /// <param name="runtime">
        /// The runtime.
        /// </param>
        internal GrainCancellationToken(Guid id, bool canceled, IGrainCancellationTokenRuntime runtime = null) : this(id)
        {
            _cancellationTokenRuntime = runtime;
            if (canceled)
            {
                // we Cancel _cancellationTokenSource just "to store" the cancelled state.
                _cancellationTokenSource.Cancel();
            }
        }

        /// <summary>
        /// Gets the unique id of the token
        /// </summary>
        internal Guid Id { get; private set; }

        /// <summary>
        /// Gets the underlying cancellation token.
        /// </summary>
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        /// <summary>
        /// Gets a value indicating if cancellation is requested.
        /// </summary>
        internal bool IsCancellationRequested => _cancellationTokenSource.IsCancellationRequested;

        /// <summary>
        /// Cancels the cancellation token.
        /// </summary>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        internal Task Cancel()
        {
            if (_cancellationTokenRuntime == null)
            {
                // Wrap in task
                try
                {
                    _cancellationTokenSource.Cancel();
                    return Task.CompletedTask;
                }
                catch (Exception exception)
                {
                    var completion = new TaskCompletionSource<object>();
                    completion.TrySetException(exception);
                    return completion.Task;
                }
            }

            return _cancellationTokenRuntime.Cancel(Id, _cancellationTokenSource, _targetGrainReferences);
        }

        /// <summary>
        /// Subscribes the provided grain reference to cancellation notifications.
        /// </summary>
        /// <param name="runtime">The grain cancellation runtime.</param>
        /// <param name="grainReference">The grain reference to add.</param>
        internal void AddGrainReference(IGrainCancellationTokenRuntime runtime, GrainReference grainReference)
        {
            if (_cancellationTokenRuntime == null)
                _cancellationTokenRuntime = runtime;
            _targetGrainReferences.TryAdd(grainReference.GrainId, grainReference);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cancellationTokenSource.Dispose();
        }
    }
}