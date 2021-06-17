using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Grain cancellation token
    /// </summary>
    public sealed class GrainCancellationToken : IDisposable
    {
        [NonSerialized]
        private readonly CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// References to remote grains to which this token was passed.
        /// </summary>
        [NonSerialized]
        private readonly ConcurrentDictionary<GrainId, GrainReference> _targetGrainReferences;

        [NonSerialized]
        private IGrainCancellationTokenRuntime _cancellationTokenRuntime;

        /// <summary>
        /// Initializes the <see cref="T:Orleans.GrainCancellationToken"/>.
        /// </summary>
        internal GrainCancellationToken(Guid id)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            Id = id;
            _targetGrainReferences = new ConcurrentDictionary<GrainId, GrainReference>();
        }


        /// <summary>
        /// Initializes the <see cref="T:Orleans.GrainCancellationToken"/>.
        /// </summary>
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
        /// Unique id of concrete token
        /// </summary>
        internal Guid Id { get; private set; }

        /// <summary>
        /// Underlying cancellation token
        /// </summary>
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        internal bool IsCancellationRequested => _cancellationTokenSource.IsCancellationRequested;

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