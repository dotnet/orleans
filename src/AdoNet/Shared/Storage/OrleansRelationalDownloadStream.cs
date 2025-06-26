using System;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

#if CLUSTERING_ADONET
namespace Orleans.Clustering.AdoNet.Storage
#elif PERSISTENCE_ADONET
namespace Orleans.Persistence.AdoNet.Storage
#elif REMINDERS_ADONET
namespace Orleans.Reminders.AdoNet.Storage
#elif STREAMING_ADONET
namespace Orleans.Streaming.AdoNet.Storage
#elif GRAINDIRECTORY_ADONET
namespace Orleans.GrainDirectory.AdoNet.Storage
#elif TESTER_SQLUTILS
namespace Orleans.Tests.SqlUtils
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// This is a chunked read implementation for ADO.NET providers which do
    /// not otherwise implement <see cref="DbDataReader.GetStream(int)"/> natively.
    /// </summary>
    internal class OrleansRelationalDownloadStream : Stream
    {
        /// <summary>
        /// A cached task as if there are multiple rounds of reads, it is likely
        /// the bytes read is the same. This saves one allocation.
        /// </summary>
        private Task<int> _lastTask;

        /// <summary>
        /// The reader to use to read from the database.
        /// </summary>
        private DbDataReader _reader;

        /// <summary>
        /// The position in the overall stream.
        /// </summary>
        private long _position;

        /// <summary>
        /// The column ordinal to read from.
        /// </summary>
        private readonly int _ordinal;

        /// <summary>
        /// The total number of bytes in the column.
        /// </summary>
        private readonly long _totalBytes;

        /// <summary>
        /// The internal byte array buffer size used in .CopyToAsync.
        /// This size is just a guess and is likely dependent on the database
        /// tuning settings (e.g. read_buffer_size in case of MySQL).
        /// </summary>
        private const int InternalReadBufferLength = 4092;

        /// <summary>
        /// Initializes a new <see cref="OrleansRelationalDownloadStream"/> instance.
        /// </summary>
        /// <param name="reader">The reader to use to read from the database.</param>
        /// <param name="ordinal">The column ordinal to read from.</param>
        public OrleansRelationalDownloadStream(DbDataReader reader, int ordinal)
        {
            _reader = reader;
            _ordinal = ordinal;

            //This return the total length of the column pointed by the ordinal.
            _totalBytes = reader.GetBytes(ordinal, 0, null, 0, 0);
        }

        /// <inheritdoc/>
        public override bool CanRead => _reader != null && (!_reader.IsClosed);

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanTimeout => true;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length => _totalBytes;

        /// <inheritdoc/>
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override void Flush() => throw new NotSupportedException();

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            //This will throw with the same parameter names if the parameters are not valid.
            ValidateReadParameters(buffer, offset, count);

            try
            {
                int length = Math.Min(count, (int)(_totalBytes - _position));
                long bytesRead = 0;
                if (length > 0)
                {
                    bytesRead = _reader.GetBytes(_ordinal, _position, buffer, offset, length);
                    _position += bytesRead;
                }

                return (int)bytesRead;
            }
            catch (DbException dex)
            {
                //It's not OK to throw non-IOExceptions from a Stream.
                throw new IOException(dex.Message, dex);
            }
        }

        /// <inheritdoc/>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            //This will throw with the same parameter names if the parameters are not valid.
            ValidateReadParameters(buffer, offset, count);

            if (cancellationToken.IsCancellationRequested)
            {
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs.SetCanceled(cancellationToken);
                return tcs.Task;
            }

            try
            {
                //The last used task is saved in order to avoid one allocation when the number of bytes read
                //will likely be the same multiple times.
                int bytesRead = Read(buffer, offset, count);
                var ret = _lastTask != null && bytesRead == _lastTask.Result ? _lastTask : (_lastTask = Task.FromResult(bytesRead));

                return ret;
            }
            catch (Exception e)
            {
                //Due to call to Read, this is for sure a IOException and can be thrown out.
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs.SetException(e);

                return tcs.Task;
            }
        }

        /// <inheritdoc/>
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                byte[] buffer = new byte[InternalReadBufferLength];
                int bytesRead;
                while ((bytesRead = Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader = null;
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Checks the parameters passed into a ReadAsync() or Read() are valid.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        private static void ValidateReadParameters(byte[] buffer, long offset, long count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            if (checked(offset + count) > buffer.Length)
            {
                throw new ArgumentException("Invalid offset length");
            }
        }
    }
}

#nullable restore
