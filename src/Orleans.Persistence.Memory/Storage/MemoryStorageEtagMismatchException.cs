using System;
using System.Runtime.Serialization;

namespace Orleans.Storage.Internal
{
    /// <summary>Exception used to communicate with the storage provider, so that it throws this exception to its caller.</summary>
    [Serializable]
    [GenerateSerializer]
    internal class MemoryStorageEtagMismatchException : Exception
    {
        /// <summary>The Etag value currently held in persistent storage.</summary>
        [Id(0)]
        public string StoredEtag { get; private set; }

        /// <summary>The Etag value currently help in memory, and attempting to be updated.</summary>
        [Id(1)]
        public string ReceivedEtag { get; private set; }

        public MemoryStorageEtagMismatchException(string storedEtag, string receivedEtag)
        {
            StoredEtag = storedEtag;
            ReceivedEtag = receivedEtag;
        }

        protected MemoryStorageEtagMismatchException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.StoredEtag = info.GetString(nameof(StoredEtag));
            this.ReceivedEtag = info.GetString(nameof(ReceivedEtag));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            info.AddValue(nameof(StoredEtag), this.StoredEtag);
            info.AddValue(nameof(ReceivedEtag), this.ReceivedEtag);
            base.GetObjectData(info, context);
        }

        public InconsistentStateException AsInconsistentStateException()
        {
            var message = $"e-Tag mismatch in Memory Storage. Stored = { StoredEtag ?? "null"} Received = {ReceivedEtag}";
            return new InconsistentStateException(message, StoredEtag, ReceivedEtag);
        }
    }
}
