using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Security
{
    internal class DuplexPipeStream : Stream
    {
        private readonly PipeReader _input;
        private readonly PipeWriter _output;
        private readonly bool _throwOnCancelled;
        private volatile bool _cancelCalled;

        public DuplexPipeStream(PipeReader input, PipeWriter output, bool throwOnCancelled = false)
        {
            _input = input;
            _output = output;
            _throwOnCancelled = throwOnCancelled;
        }

        public void CancelPendingRead()
        {
            _cancelCalled = true;
            _input.CancelPendingRead();
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length
        {
            get
            {
                throw new NotSupportedException();
            }
        }

        public override long Position
        {
            get
            {
                throw new NotSupportedException();
            }
            set
            {
                throw new NotSupportedException();
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // ValueTask uses .GetAwaiter().GetResult() if necessary
            // https://github.com/dotnet/corefx/blob/f9da3b4af08214764a51b2331f3595ffaf162abe/src/System.Threading.Tasks.Extensions/src/System/Threading/Tasks/ValueTask.cs#L156
            return ReadAsyncInternal(new Memory<byte>(buffer, offset, count), CancellationToken.None).Result;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsyncInternal(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

#if NETCOREAPP
        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            return ReadAsyncInternal(destination, cancellationToken);
        }
#endif

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer != null)
            {
                _output.Write(new ReadOnlySpan<byte>(buffer, offset, count));
            }

            await _output.FlushAsync(cancellationToken);
        }

#if NETCOREAPP
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            _output.Write(source.Span);
            await _output.FlushAsync(cancellationToken);
        }
#endif

        public override void Flush()
        {
            FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return WriteAsync(null, 0, 0, cancellationToken);
        }

        private async ValueTask<int> ReadAsyncInternal(Memory<byte> destination, CancellationToken cancellationToken)
        {
            while (true)
            {
                var result = await _input.ReadAsync(cancellationToken);
                var readableBuffer = result.Buffer;
                try
                {
                    if (_throwOnCancelled && result.IsCanceled && _cancelCalled)
                    {
                        // Reset the bool
                        _cancelCalled = false;
                        throw new OperationCanceledException();
                    }

                    if (!readableBuffer.IsEmpty)
                    {
                        // buffer.Count is int
                        var count = (int)Math.Min(readableBuffer.Length, destination.Length);
                        readableBuffer = readableBuffer.Slice(0, count);
                        readableBuffer.CopyTo(destination.Span);
                        return count;
                    }

                    if (result.IsCompleted)
                    {
                        return 0;
                    }
                }
                finally
                {
                    _input.AdvanceTo(readableBuffer.End, readableBuffer.End);
                }
            }
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            var task = ReadAsync(buffer, offset, count, CancellationToken.None, state);
            if (callback != null)
            {
                task.ContinueWith(t => callback.Invoke(t));
            }
            return task;
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            return ((Task<int>)asyncResult).GetAwaiter().GetResult();
        }

        private Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken, object state)
        {
            var tcs = new TaskCompletionSource<int>(state);
            var task = ReadAsync(buffer, offset, count, cancellationToken);
            task.ContinueWith((task2, state2) =>
            {
                var tcs2 = (TaskCompletionSource<int>)state2;
                if (task2.IsCanceled)
                {
                    tcs2.SetCanceled();
                }
                else if (task2.IsFaulted)
                {
                    tcs2.SetException(task2.Exception);
                }
                else
                {
                    tcs2.SetResult(task2.Result);
                }
            }, tcs, cancellationToken);
            return tcs.Task;
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            var task = WriteAsync(buffer, offset, count, CancellationToken.None, state);
            if (callback != null)
            {
                task.ContinueWith(t => callback.Invoke(t));
            }
            return task;
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            ((Task<object>)asyncResult).GetAwaiter().GetResult();
        }

        private Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken, object state)
        {
            var tcs = new TaskCompletionSource<object>(state);
            var task = WriteAsync(buffer, offset, count, cancellationToken);
            task.ContinueWith((task2, state2) =>
            {
                var tcs2 = (TaskCompletionSource<object>)state2;
                if (task2.IsCanceled)
                {
                    tcs2.SetCanceled();
                }
                else if (task2.IsFaulted)
                {
                    tcs2.SetException(task2.Exception);
                }
                else
                {
                    tcs2.SetResult(null);
                }
            }, tcs, cancellationToken);
            return tcs.Task;
        }
    }
}
