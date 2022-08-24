using System;
using System.Collections.Generic;

namespace Orleans.Transactions
{
    /// <summary>
    /// A queue data structure that stores transaction records in a circular buffer, sorted by timestamps.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal struct CommitQueue<T>
    {
        private const int DefaultCapacity = 8;
        private TransactionRecord<T>[] buffer;
        private int pos;

        public int Count { get; private set; }

        // Indexer to provide read/write access to the file.
        public TransactionRecord<T> this[int index]
        {
            get
            {
                if (index < 0 || index > (Count - 1))
                    throw new ArgumentOutOfRangeException(nameof(index));

                return buffer[(pos + index) % buffer.Length];
            }
        }

        public IEnumerable<TransactionRecord<T>> Elements
        {
            get
            {
                if (buffer != null)
                {
                    for (int i = 0; i < Count; i++)
                        yield return buffer[(pos + i) % buffer.Length];
                }
            }
        }

        public TransactionRecord<T> First => buffer[pos];

        public TransactionRecord<T> Last => buffer[(pos + Count - 1) % buffer.Length];

        public void Add(TransactionRecord<T> entry)
        {
            // ensure we have room
            if (buffer == null)
            {
                buffer = new TransactionRecord<T>[DefaultCapacity];
            }
            else if (Count == buffer.Length)
            {
                var newbuffer = new TransactionRecord<T>[buffer.Length * 2];
                Array.Copy(buffer, pos, newbuffer, 0, buffer.Length - pos);
                Array.Copy(buffer, 0, newbuffer, buffer.Length - pos, pos);
                buffer = newbuffer;
                pos = 0;
            }

            if (Count > 0 && buffer[(pos + Count - 1) % buffer.Length].Timestamp > entry.Timestamp)
                throw new ArgumentException($"elements must be added in timestamp order, but {entry.Timestamp:o} is before {buffer[(pos + Count - 1) % buffer.Length].Timestamp:o}", nameof(entry));

            // add the element
            buffer[(pos + Count) % buffer.Length] = entry;
            Count++;
        }

        public void Clear()
        {
            for (int i = 0; i < Count; i++)
                buffer[(pos + i) % buffer.Length] = null;
            Count = 0;
            pos = 0;
        }

        public void RemoveFromFront(int howMany)
        {
            if (howMany <= 0)
            {
                throw new ArgumentException("Value must be greater than zero", nameof(howMany));
            }

            if (buffer == null || howMany > Count)
            {
                throw new ArgumentException("cannot remove more elements than are in the queue", nameof(howMany));
            }

            // clear entries so they can ge GCd
            for (int i = 0; i < howMany; i++)
                buffer[(pos + i) % buffer.Length] = null;

            pos = (pos + howMany) % buffer.Length;

            Count -= howMany;
        }

        public void RemoveFromBack(int howMany)
        {
            if (howMany > 0 && (buffer == null || howMany > Count))
                throw new ArgumentException("cannot remove more elements than are in the queue", nameof(howMany));

            // clear entries so they can ge GCd
            for (int i = 0; i < howMany; i++)
                buffer[(pos + Count - i - 1) % buffer.Length] = null;

            Count -= howMany;
        }

        public int Find(Guid TransactionId, DateTime key)
        {
            // do a binary search
            int left = 0;
            int right = Count;
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
