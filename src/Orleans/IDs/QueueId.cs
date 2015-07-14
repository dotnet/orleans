/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using Orleans.Runtime;
using Orleans.Concurrency;

namespace Orleans.Streams
{
    /// <summary>
    /// Identifier of a durable queue.
    /// Used by Orlens streaming extensions.
    /// </summary>
    [Serializable]
    [Immutable]
    public class QueueId : IRingIdentifier<QueueId>, IEquatable<QueueId>, IComparable<QueueId>
    {
        private static readonly Lazy<Interner<QueueId, QueueId>> queueIdInternCache = new Lazy<Interner<QueueId, QueueId>>(
                    () => new Interner<QueueId, QueueId>(InternerConstants.SIZE_LARGE, InternerConstants.DefaultCacheCleanupFreq));

        private readonly string queueNamePrefix;
        private readonly uint queueId;
        private readonly uint uniformHashCache;

        // TODO: Need to integrate with Orleans serializer to really use Interner.
        private QueueId(string queuePrefix, uint id, uint hash)
        {
            queueNamePrefix = queuePrefix;
            queueId = id;
            uniformHashCache = hash;
        }

        public static QueueId GetQueueId(string queuePrefix, uint id, uint hash)
        {
            return FindOrCreateQueueId(queuePrefix, id, hash);
        }

        private static QueueId FindOrCreateQueueId(string queuePrefix, uint id, uint hash)
        {
            var key = new QueueId(queuePrefix, id, hash);
            return queueIdInternCache.Value.FindOrCreate(key, () => key);
        }

        #region IComparable<QueueId> Members

        public int CompareTo(QueueId other)
        {
            int cmp = queueId.CompareTo(other.queueId);
            if (cmp != 0) return cmp;

            cmp = String.Compare(queueNamePrefix, other.queueNamePrefix, StringComparison.Ordinal);
            if (cmp == 0) cmp = uniformHashCache.CompareTo(other.uniformHashCache);
            
            return cmp;
        }

        #endregion

        #region IEquatable<QueueId> Members

        public virtual bool Equals(QueueId other)
        {
            return other != null && queueId == other.queueId && queueNamePrefix.Equals(other.queueNamePrefix) && uniformHashCache == other.uniformHashCache;
        }

        #endregion

        public override bool Equals(object obj)
        {
            return this.Equals(obj as QueueId);
        }

        public override int GetHashCode()
        {
            return (int)queueId ^ queueNamePrefix.GetHashCode() ^ (int)uniformHashCache;
        }

        public uint GetUniformHashCode()
        {
            return uniformHashCache;
        }

        public override string ToString()
        {
            return String.Format("{0}-{1}", queueNamePrefix.ToLower(), queueId.ToString());
        }

        public string ToStringWithHashCode()
        {
            return String.Format("{0}-0x{1, 8:X8}", this.ToString(), this.GetUniformHashCode());
        }
    }
}