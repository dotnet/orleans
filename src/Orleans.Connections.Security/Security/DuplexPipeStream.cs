using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Security
{
    internal class DuplexPipeStream : Stream
    {
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public DuplexPipeStream(IDuplexPipe pipe)
        {
            _reader = pipe.Input;
            _writer = pipe.Output;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Complete();
                _writer.Complete();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _reader.CompleteAsync().ConfigureAwait(false);
            await _writer.CompleteAsync().ConfigureAwait(false);
        }

        public override void Flush()
        {
            FlushAsync().GetAwaiter().GetResult();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushResult r = await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (r.IsCanceled) throw new OperationCanceledException(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);

            ValueTask<int> t = ReadAsync(buffer.AsMemory(offset, count));
            return
                t.IsCompleted ? t.GetAwaiter().GetResult() :
                t.AsTask().GetAwaiter().GetResult();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);

            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (result.IsCanceled)
            {
                throw new OperationCanceledException();
            }

            ReadOnlySequence<byte> sequence = result.Buffer;
            long bufferLength = sequence.Length;
            SequencePosition consumed = sequence.Start;

            try
            {
                if (bufferLength != 0)
                {
                    int actual = (int)Math.Min(bufferLength, buffer.Length);

                    ReadOnlySequence<byte> slice = actual == bufferLength ? sequence : sequence.Slice(0, actual);
                    consumed = slice.End;
                    slice.CopyTo(buffer.Span);

                    return actual;
                }

                if (result.IsCompleted)
                {
                    return 0;
                }
            }
            finally
            {
                _reader.AdvanceTo(consumed);
            }

            // This is a buggy PipeReader implementation that returns 0 byte reads even though the PipeReader
            // isn't completed or canceled.
            throw new InvalidOperationException("Read zero bytes unexpectedly");
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            return TaskToApm.Begin(ReadAsync(buffer, offset, count), callback, state);
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            return TaskToApm.End<int>(asyncResult);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);

            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            FlushResult r = await _writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (r.IsCanceled) throw new OperationCanceledException(cancellationToken);
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            return TaskToApm.Begin(WriteAsync(buffer, offset, count), callback, state);
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            TaskToApm.End(asyncResult);
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            return _reader.CopyToAsync(destination, cancellationToken);
        }

        /// <summary>
        /// Provides support for efficiently using Tasks to implement the APM (Begin/End) pattern.
        /// </summary>
        internal static class TaskToApm
        {
            /// <summary>
            /// Marshals the Task as an IAsyncResult, using the supplied callback and state
            /// to implement the APM pattern.
            /// </summary>
            /// <param name="task">The Task to be marshaled.</param>
            /// <param name="callback">The callback to be invoked upon completion.</param>
            /// <param name="state">The state to be stored in the IAsyncResult.</param>
            /// <returns>An IAsyncResult to represent the task's asynchronous operation.</returns>
            public static IAsyncResult Begin(Task task, AsyncCallback callback, object state) =>
                new TaskAsyncResult(task, state, callback);

            /// <summary>Processes an IAsyncResult returned by Begin.</summary>
            /// <param name="asyncResult">The IAsyncResult to unwrap.</param>
            public static void End(IAsyncResult asyncResult)
            {
                if (GetTask(asyncResult) is Task t)
                {
                    t.GetAwaiter().GetResult();
                    return;
                }

                ThrowArgumentException(asyncResult);
            }

            /// <summary>Processes an IAsyncResult returned by Begin.</summary>
            /// <param name="asyncResult">The IAsyncResult to unwrap.</param>
            public static TResult End<TResult>(IAsyncResult asyncResult)
            {
                if (GetTask(asyncResult) is Task<TResult> task)
                {
                    return task.GetAwaiter().GetResult();
                }

                ThrowArgumentException(asyncResult);
                return default!; // unreachable
            }

            /// <summary>Gets the task represented by the IAsyncResult.</summary>
            public static Task GetTask(IAsyncResult asyncResult) => (asyncResult as TaskAsyncResult)?._task;

            /// <summary>Throws an argument exception for the invalid <paramref name="asyncResult"/>.</summary>
            private static void ThrowArgumentException(IAsyncResult asyncResult) =>
                throw (asyncResult is null ?
                    new ArgumentNullException(nameof(asyncResult)) :
                    new ArgumentException(null, nameof(asyncResult)));

            /// <summary>Provides a simple IAsyncResult that wraps a Task.</summary>
            /// <remarks>
            /// We could use the Task as the IAsyncResult if the Task's AsyncState is the same as the object state,
            /// but that's very rare, in particular in a situation where someone cares about allocation, and always
            /// using TaskAsyncResult simplifies things and enables additional optimizations.
            /// </remarks>
            internal sealed class TaskAsyncResult : IAsyncResult
            {
                /// <summary>The wrapped Task.</summary>
                internal readonly Task _task;
                /// <summary>Callback to invoke when the wrapped task completes.</summary>
                private readonly AsyncCallback _callback;

                /// <summary>Initializes the IAsyncResult with the Task to wrap and the associated object state.</summary>
                /// <param name="task">The Task to wrap.</param>
                /// <param name="state">The new AsyncState value.</param>
                /// <param name="callback">Callback to invoke when the wrapped task completes.</param>
                internal TaskAsyncResult(Task task, object state, AsyncCallback callback)
                {
                    Debug.Assert(task != null);
                    _task = task;
                    AsyncState = state;

                    if (task.IsCompleted)
                    {
                        // Synchronous completion.  Invoke the callback.  No need to store it.
                        CompletedSynchronously = true;
                        callback?.Invoke(this);
                    }
                    else if (callback != null)
                    {
                        // Asynchronous completion, and we have a callback; schedule it. We use OnCompleted rather than ContinueWith in
                        // order to avoid running synchronously if the task has already completed by the time we get here but still run
                        // synchronously as part of the task's completion if the task completes after (the more common case).
                        _callback = callback;
                        _task.ConfigureAwait(continueOnCapturedContext: false)
                             .GetAwaiter()
                             .OnCompleted(InvokeCallback); // allocates a delegate, but avoids a closure
                    }
                }

                /// <summary>Invokes the callback.</summary>
                private void InvokeCallback()
                {
                    Debug.Assert(!CompletedSynchronously);
                    Debug.Assert(_callback != null);
                    _callback.Invoke(this);
                }

                /// <summary>Gets a user-defined object that qualifies or contains information about an asynchronous operation.</summary>
                public object AsyncState { get; }
                /// <summary>Gets a value that indicates whether the asynchronous operation completed synchronously.</summary>
                /// <remarks>This is set lazily based on whether the <see cref="_task"/> has completed by the time this object is created.</remarks>
                public bool CompletedSynchronously { get; }
                /// <summary>Gets a value that indicates whether the asynchronous operation has completed.</summary>
                public bool IsCompleted => _task.IsCompleted;
                /// <summary>Gets a <see cref="WaitHandle"/> that is used to wait for an asynchronous operation to complete.</summary>
                public WaitHandle AsyncWaitHandle => ((IAsyncResult)_task).AsyncWaitHandle;
            }
        }
    }
}
