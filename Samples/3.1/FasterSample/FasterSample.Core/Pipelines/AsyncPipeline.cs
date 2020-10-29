using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FasterSample.Core.Properties;
using Microsoft.Extensions.Logging;

namespace FasterSample.Core.Pipelines
{
    internal class AsyncPipeline : IAsyncPipeline, IDisposable
    {
        /// <summary>
        /// Used to log user task faults.
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Controls the current capacity of the pipeline.
        /// </summary>
        private readonly SemaphoreSlim _semaphore;

        /// <summary>
        /// Creates a new pipeline with the specified capacity.
        /// </summary>
        public AsyncPipeline(int capacity, ILogger<AsyncPipeline> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _semaphore = new SemaphoreSlim(capacity);
            _onTaskCompletion = OnTaskCompletion;
        }

        /// <summary>
        /// Caches the completion delegate to avoid redundant allocations.
        /// </summary>
        private readonly Action<Task> _onTaskCompletion;

        /// <summary>
        /// Allows early cancelling of any leftover completion tasks when the pipeline is disposed.
        /// </summary>
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        /// <summary>
        /// Tracks active tasks and completions so we can support awaiting for them all.
        /// </summary>
        private readonly ConcurrentDictionary<Task, Task> _active = new ConcurrentDictionary<Task, Task>();

        /// <summary>
        /// Caches the error log delegate to avoid redundant allocations.
        /// </summary>
        private readonly Action<ILogger, Exception> _logError = LoggerMessage.Define(LogLevel.Error, default, Resources.Log_PipelineTaskFaulted);

        /// <summary>
        /// Adds an action to the pipeline and returns the task for it once it is started.
        /// If there is enough capacity then the action will be started immediately and this method will also complete immediately.
        /// If there is not enough capacity then this method will wait until there is enough capacity before starting the action.
        /// This behaviour makes it safe to use this method in an long-lived loop that keeps generating new actions.
        /// The pipeline itself will handle action parallelism as needed.
        /// </summary>
        /// <param name="action">The action to add to the pipeline.</param>
        /// <param name="cancellationToken">A token that is used to cancel the addition of the task into the pipeline.</param>
        public Task<Task> AddAsync(Func<Task> action, CancellationToken cancellationToken = default)
        {
            if (action is null) throw new ArgumentNullException(nameof(action));

            EnsureNotDisposed();

            return InnerAddAsync(action, cancellationToken);
        }

        /// <summary>
        /// The async state machine portion of <see cref="AddAsync(Func{Task}, CancellationToken)"/>.
        /// </summary>
        private async Task<Task> InnerAddAsync(Func<Task> action, CancellationToken cancellationToken = default)
        {
            // wait for a spot
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            // we can now start the task on the thread pool
            var task = Task.Run(action, cancellationToken);

            // keep track of the task and wire up the future completion of this task so the semaphore is always released and the tracker is always cleared
            _active[task] = task.ContinueWith(_onTaskCompletion, _cancellation.Token, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            // make the task available to the user
            return task;
        }

        /// <summary>
        /// Handles completion of a user task, regardless of status.
        /// </summary>
        private void OnTaskCompletion(Task task)
        {
            // release the semaphore so another task can start
            _semaphore.Release();

            // stop tracking this task as active
            _active.TryRemove(task, out var _);

            // observe any exception so it doesn't escalate to the finalizer thread
            // the user is still free to observe it by awaiting on the task returned by the add method
            if (task.Exception != null)
            {
                _logError(_logger, task.Exception);
            }
        }

        /// <summary>
        /// Returns a task that completes when all currently started tasks complete.
        /// </summary>
        public async Task WhenAll()
        {
            // wait for all active tasks to complete and for their completion handlers as well
            // there is a possibility of missing active tasks here if the user is still adding tasks at the same time of calling this method
            // we accept this misuse of the class to avoid locking - the user can always call this method again to await for newly added tasks
            foreach (var task in _active)
            {
                // wait for each completion handler to complete - this includes the task it depends on
                // the completion handler will never throw exceptions unless something is wrong with the logging service itself
                // we wait for the handler instead of the user task in order to ensure any user errors are logged before this method completes
                await task.Value.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Ensures this pipeline is not disposed.
        /// </summary>
        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(null);
            }
        }

        #region Disposable

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            _disposed = true;

            if (disposing)
            {
                // this will also cause any waiting add calls to fail
                _semaphore.Dispose();

                // this will request cancellation of any leftover completion tasks
                _cancellation.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~AsyncPipeline()
        {
            Dispose(false);
        }

        #endregion Disposable
    }
}