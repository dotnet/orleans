using System;
using System.Runtime.Serialization;

namespace Orleans.Storage.Internal
{
    /// <summary>Exception used to communicate with the storage provider, so that it throws this exception to its caller.</summary>
    [Serializable]
    internal class MemoryStorageEtagMismatchException : Exception
    {
        /// <summary>The Etag value currently held in persistent storage.</summary>
        public string StoredEtag { get; private set; }

        /// <summary>The Etag value currently help in memory, and attempting to be updated.</summary>
        public string ReceivedEtag { get; private set; }

        public MemoryStorageEtagMismatchException(string storedEtag, string receivedEtag)
        {
            StoredEtag = storedEtag;
            ReceivedEtag = receivedEtag;
        }

#if !NETSTANDARD
        protected MemoryStorageEtagMismatchException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
#endif

        public InconsistentStateException AsInconsistentStateException()
        {
            var message = $"e-Tag mismatch in Memory Storage. Stored = { StoredEtag ?? "null"} Received = {ReceivedEtag}";
            return new InconsistentStateException(message, StoredEtag, ReceivedEtag);
        }
    }
}
