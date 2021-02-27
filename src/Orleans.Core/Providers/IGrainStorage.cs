using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Orleans.Runtime;
using System.Net;

namespace Orleans.Storage
{
    /// <summary>
    /// Interface to be implemented for a storage able to read and write Orleans grain state data.
    /// </summary>
    public interface IGrainStorage
    {
        /// <summary>Read data function for this storage instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">State data object to be populated for this grain.</param>
        /// <returns>Completion promise for the Read operation on the specified grain.</returns>
        Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);

        /// <summary>Write data function for this storage instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">State data object to be written for this grain.</param>
        /// <returns>Completion promise for the Write operation on the specified grain.</returns>
        Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);

        /// <summary>Delete / Clear data function for this storage instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">Copy of last-known state data object for this grain.</param>
        /// <returns>Completion promise for the Delete operation on the specified grain.</returns>
        Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);
    }

    /// <summary>
    /// Interface to be optionally implemented by storage to return richer exception details.
    /// TODO: Remove this interface.  Move to decorator pattern for monitoring purposes. - jbragg
    /// </summary>
    public interface IRestExceptionDecoder
    {
        /// <summary>
        /// Decode details of the exception
        /// </summary>
        /// <param name="e">Exception to decode</param>
        /// <param name="httpStatusCode">HTTP status code for the error</param>
        /// <param name="restStatus">REST status for the error</param>
        /// <param name="getExtendedErrors">Whether or not to extract REST error code</param>
        /// <returns></returns>
        bool DecodeException(Exception e, out HttpStatusCode httpStatusCode, out string restStatus, bool getExtendedErrors = false);
    }

    /// <summary>
    /// Exception thrown whenever a grain call is attempted with a bad / missing storage configuration settings for that grain.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class BadGrainStorageConfigException : BadProviderConfigException
    {
        public BadGrainStorageConfigException()
        { }
        public BadGrainStorageConfigException(string msg)
            : base(msg)
        { }
        public BadGrainStorageConfigException(string msg, Exception exc)
            : base(msg, exc)
        { }

        protected BadGrainStorageConfigException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }

    /// <summary>
    /// Exception thrown when a storage detects an Etag inconsistency when attempting to perform a WriteStateAsync operation.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class InconsistentStateException : OrleansException
    {
        /// <summary>
        /// Whether or not this exception occurred on the current activation.
        /// </summary>
        [Id(0)]
        internal bool IsSourceActivation { get; set; } = true;

        /// <summary>The Etag value currently held in persistent storage.</summary>
        [Id(1)]
        public string StoredEtag { get; private set; }

        /// <summary>The Etag value currently help in memory, and attempting to be updated.</summary>
        [Id(2)]
        public string CurrentEtag { get; private set; }

        public InconsistentStateException()
        { }
        public InconsistentStateException(string msg)
            : base(msg)
        { }
        public InconsistentStateException(string msg, Exception exc)
            : base(msg, exc)
        { }

        protected InconsistentStateException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.StoredEtag = info.GetString(nameof(StoredEtag));
            this.CurrentEtag = info.GetString(nameof(CurrentEtag));
            this.IsSourceActivation = info.GetBoolean(nameof(this.IsSourceActivation));
        }

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

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            info.AddValue(nameof(StoredEtag), this.StoredEtag);
            info.AddValue(nameof(CurrentEtag), this.CurrentEtag);
            info.AddValue(nameof(this.IsSourceActivation), this.IsSourceActivation);
            base.GetObjectData(info, context);
        }
    }
}
