using System;
using System.Threading.Tasks;
using System.Threading;

namespace Orleans
{
    /// <summary>
    /// An analogue to <see cref="CancellationTokenSource"/> which can be sent between grains.
    /// </summary>
    public sealed class GrainCancellationTokenSource : IDisposable
    {
        /// <summary>
        /// The underlying grain cancellation token.
        /// </summary>
        private readonly GrainCancellationToken _grainCancellationToken;

        /// <summary>
        /// Initializes the <see cref="T:Orleans.GrainCancellationTokenSource"/>.
        /// </summary>
        public GrainCancellationTokenSource()
        {
            _grainCancellationToken = new GrainCancellationToken(Guid.NewGuid());
        }

        /// <summary>
        /// Gets the <see cref="GrainCancellationTokenSource">CancellationToken</see>
        /// associated with this <see cref="GrainCancellationTokenSource"/>.
        /// </summary>
        /// <value>The <see cref="GrainCancellationToken">CancellationToken</see>
        /// associated with this <see cref="GrainCancellationToken"/>.</value>
        public GrainCancellationToken Token
        {
            get { return _grainCancellationToken; }
        }

        /// <summary>
        /// Gets a value indicating whether cancellation has been requested.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This property indicates whether cancellation has been requested for this token source, such as due to a call to its <see cref="Cancel()" /> method.
        /// </para>
        /// <para>
        /// If this property returns true, it only guarantees that cancellation has been requested. It does not guarantee that every handler registered with the corresponding token has finished executing, nor that
        /// cancellation requests have finished propagating to all registered handlers and remote targets. Additional synchronization may be required, particularly in situations where related objects are being canceled
        /// concurrently.
        /// </para>
        /// </remarks>
        public bool IsCancellationRequested
        {
            get { return _grainCancellationToken.IsCancellationRequested; }
        }

        /// <summary>
        /// Communicates a request for cancellation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The associated <see cref="GrainCancellationToken" /> will be notified of the cancellation and will transition to a state where
        /// <see cref="GrainCancellationToken.CancellationToken"><see cref="IsCancellationRequested"/></see> returns true. Any callbacks or cancelable operations registered with the
        /// <see cref="CancellationToken" /> will be executed.
        /// </para>
        /// <para>
        /// Cancelable operations and callbacks registered with the token should not <see langword="throw"/> exceptions. However, this overload of <see cref="Cancel"/> will aggregate any exceptions thrown into a
        /// <see cref="AggregateException" /> , such that one callback throwing an exception will not prevent other registered callbacks from being executed.
        /// </para>
        /// <para>The <see cref="System.Threading.ExecutionContext" /> that was captured when each callback was registered will be reestablished when the callback is invoked.</para>
        /// </remarks>
        /// <exception cref="AggregateException">An aggregate exception containing all the exceptions thrown by the registered callbacks on the associated <see cref="GrainCancellationToken" /> .</exception>
        /// <exception cref="ObjectDisposedException">This <see cref="GrainCancellationTokenSource" /> has been disposed.</exception>
        public Task Cancel()
        {
            return _grainCancellationToken.Cancel();
        }

        /// <summary>
        /// Releases the resources used by this <see cref="T:Orleans.Async.GrainCancellationTokenSource" />.
        /// </summary>
        /// <remarks>
        /// This method is not thread-safe for any other concurrent calls.
        /// </remarks>
        public void Dispose()
        {
            _grainCancellationToken.Dispose();
        }
    }
}