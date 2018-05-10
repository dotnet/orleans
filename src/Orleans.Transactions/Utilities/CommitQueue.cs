using Orleans.Transactions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    /// <summary>
    /// A deque data structure that stores transaction records in a circular buffer, sorted by timestamps.
    /// </summary>
    /// <typeparam name="E"></typeparam>
    internal struct CommitQueue<E>
    {
        private TransactionRecord<E>[] buffer;
        private int count;
        private int pos;

        private const int defaultCapacity = 8;

        public int Count { get { return count; } }

        // Indexer to provide read/write access to the file.
        public TransactionRecord<E> this[int index]
        {
            get
            {
                if (index < 0 || index > (count - 1))
                    throw new ArgumentOutOfRangeException(nameof(index));

                return buffer[(pos + index) % buffer.Length];
            }
        }

        public IEnumerable<TransactionRecord<E>> Elements
        {
            get
            {
                if (buffer != null)
                {
                    for (int i = 0; i < count; i++)
                        yield return buffer[(pos + i) % buffer.Length];
                }
            }
        }

        public TransactionRecord<E> First => buffer[pos];

        public TransactionRecord<E> Last => buffer[(pos + count - 1) % buffer.Length];

        public void Add(TransactionRecord<E> entry)
        {
            // ensure we have room
            if (buffer == null)
            {
                buffer = new TransactionRecord<E>[defaultCapacity];
            }
            else if (count == buffer.Length)
            {
                var newbuffer = new TransactionRecord<E>[buffer.Length * 2];
                Array.Copy(buffer, pos, newbuffer, 0, buffer.Length - pos);
                Array.Copy(buffer, 0, newbuffer, buffer.Length - pos, pos);
                buffer = newbuffer;
                pos = 0;
            }

            if (count > 0 && buffer[(pos + count - 1) % buffer.Length].Timestamp > entry.Timestamp)
                throw new ArgumentException($"elements must be added in timestamp order, but {entry.Timestamp:o} is before {buffer[(pos + count - 1) % buffer.Length].Timestamp:o}", nameof(entry));

            // add the element
            buffer[(pos + count) % buffer.Length] = entry;
            count++;
        }

        public void Clear()
        {
            for (int i = 0; i < count; i++)
                buffer[(pos + i) % buffer.Length] = null;
            count = 0;
            pos = 0;
        }

        public void RemoveFromFront(int howMany)
        {
            if (howMany > 0 && (buffer == null || howMany > count))
                throw new ArgumentException("cannot remove more elements than are in the queue", nameof(howMany));

            // clear entries so they can ge GCd
            for (int i = 0; i < howMany; i++)
                buffer[(pos + i) % buffer.Length] = null;

            pos = (pos + howMany) % buffer.Length;

            count -= howMany;
        }

        public void RemoveFromBack(int howMany)
        {
            if (howMany > 0 && (buffer == null || howMany > count))
                throw new ArgumentException("cannot remove more elements than are in the queue", nameof(howMany));

            // clear entries so they can ge GCd
            for (int i = 0; i < howMany; i++)
                buffer[(pos + count - i - 1) % buffer.Length] = null;

            count -= howMany;
        }

        public int Find(Guid TransactionId, DateTime key)
        {
            // do a binary search
            int left = 0;
            int right = count;
            while (left < right)
            {
                int mid = (left + right) / 2;
                var record = buffer[(pos + mid) % buffer.Length];
                if (record.Timestamp < key)
                {
                    left = mid + 1;
                    continue;
                }
                else if (record.Timestamp > key)
                {
                    right = mid;
                    continue;
                }
                else if (record.TransactionId == TransactionId)
                {
                    return mid;
                }
                else
                {
                    // search to the left
                    for (int j = mid - 1; j >= left; j--)
                    {
                        record = buffer[(pos + j) % buffer.Length];
                        if (record.TransactionId == TransactionId)
                            return j;
                        if (record.Timestamp != key)
                            break;
                    }
                    // search to the right
                    for (int j = mid + 1; j < right; j++)
                    {
                        record = buffer[(pos + j) % buffer.Length];
                        if (record.TransactionId == TransactionId)
                            return j;
                        if (record.Timestamp != key)
                            break;
                    }
                    return NotFound;
                }
            }

            return NotFound;
        }

        private const int NotFound = -1;
    }
}
