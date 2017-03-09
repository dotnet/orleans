using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Storage
{
    /// <summary>
    /// Exception thrown when a storage provider detects an Etag inconsistency when attemptiong to perform a WriteStateAsync operation.
    /// </summary>
    [Serializable]
    public class InconsistentStateException : OrleansException
    {
        /// <summary>The Etag value currently held in persistent storage.</summary>
        public string StoredEtag { get; private set; }

        /// <summary>The Etag value currently help in memory, and attempting to be updated.</summary>
        public string CurrentEtag { get; private set; }

        public InconsistentStateException()
        { }
        public InconsistentStateException(string msg)
            : base(msg)
        { }
        public InconsistentStateException(string msg, Exception exc)
            : base(msg, exc)
        { }
#if !NETSTANDARD
        protected InconsistentStateException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.StoredEtag = info.GetString("StoredEtag");
            this.CurrentEtag = info.GetString("CurrentEtag");
        }
#endif

        public InconsistentStateException(
            string errorMsg,
            string storedEtag,
            string currentEtag,
            Exception storageException
        ) : base(errorMsg, storageException)
        {
            StoredEtag = storedEtag;
            CurrentEtag = currentEtag;
        }

        public InconsistentStateException(
            string errorMsg,
            string storedEtag,
            string currentEtag
        )
            : this(errorMsg, storedEtag, currentEtag, null)
        { }

        public InconsistentStateException(string storedEtag, string currentEtag, Exception storageException)
            : this(storageException.Message, storedEtag, currentEtag, storageException)
        { }

        public override string ToString()
        {
            return String.Format("InconsistentStateException: {0} Expected Etag={1} Received Etag={2} {3}",
                Message, StoredEtag, CurrentEtag, InnerException);
        }

#if !NETSTANDARD
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            info.AddValue("StoredEtag", this.StoredEtag);
            info.AddValue("CurrentEtag", this.CurrentEtag);
            base.GetObjectData(info, context);
        }
#endif
    }
}