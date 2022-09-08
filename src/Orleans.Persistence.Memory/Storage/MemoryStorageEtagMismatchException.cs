using System;
using System.Runtime.Serialization;

namespace Orleans.Storage.Internal
{
    /// <summary>Exception used to communicate with the storage provider, so that it throws this exception to its caller.</summary>
    [Serializable]
    [GenerateSerializer]
    internal sealed class MemoryStorageEtagMismatchException : Exception
    {
        /// <summary>Gets the Etag value currently held in persistent storage.</summary>
        [Id(0)]
        public string StoredEtag { get; private set; }

        /// <summary>Gets the Etag value currently help in memory, and attempting to be updated.</summary>
        [Id(1)]
        public string ReceivedEtag { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryStorageEtagMismatchException"/> class.
        /// </summary>
        /// <param name="storedEtag">The stored etag.</param>
        /// <param name="receivedEtag">The received etag.</param>
        public MemoryStorageEtagMismatchException(string storedEtag, string receivedEtag)
        {
            StoredEtag = storedEtag;
            ReceivedEtag = receivedEtag;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryStorageEtagMismatchException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private MemoryStorageEtagMismatchException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.StoredEtag = info.GetString(nameof(StoredEtag));
            this.ReceivedEtag = info.GetString(nameof(ReceivedEtag));
        }

        /// <inheritdoc/>
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            info.AddValue(nameof(StoredEtag), this.StoredEtag);
            info.AddValue(nameof(ReceivedEtag), this.ReceivedEtag);
            base.GetObjectData(info, context);
        }

        /// <summary>
        /// Converts this instance into an <see cref="InconsistentStateException"/>.
        /// </summary>
        /// <returns>A new <see cref="InconsistentStateException"/>.</returns>
        public InconsistentStateException AsInconsistentStateException()
        {
            var message = $"e-Tag mismatch in Memory Storage. Stored = { StoredEtag ?? "null"} Received = {ReceivedEtag}";
            return new InconsistentStateException(message, StoredEtag, ReceivedEtag, this);
        }
    }
}
