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
        /// <param name="grainId">Grain ID</param>
        /// <param name="grainState">State data object to be populated for this grain.</param>
        /// <typeparam name="T">The grain state type.</typeparam>
        /// <returns>Completion promise for the Read operation on the specified grain.</returns>
        Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState);

        /// <summary>Write data function for this storage instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainId">Grain ID</param>
        /// <param name="grainState">State data object to be written for this grain.</param>
        /// <typeparam name="T">The grain state type.</typeparam>
        /// <returns>Completion promise for the Write operation on the specified grain.</returns>
        Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState);

        /// <summary>Delete / Clear data function for this storage instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainId">Grain ID</param>
        /// <param name="grainState">Copy of last-known state data object for this grain.</param>
        /// <typeparam name="T">The grain state type.</typeparam>
        /// <returns>Completion promise for the Delete operation on the specified grain.</returns>
        Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState);
    }

    /// <summary>
    /// Interface to be optionally implemented by storage to return richer exception details.
    /// TODO: Remove this interface.  Move to decorator pattern for monitoring purposes. - jbragg
    /// </summary>
    public interface IRestExceptionDecoder
    {
        /// <summary>
        /// Decode details of the exception.
        /// </summary>
        /// <param name="exception">Exception to decode.</param>
        /// <param name="httpStatusCode">HTTP status code for the error.</param>
        /// <param name="restStatus">REST status for the error.</param>
        /// <param name="getExtendedErrors">Whether or not to extract REST error code.</param>
        /// <returns>A value indicating whether the exception was decoded.</returns>
        bool DecodeException(Exception exception, out HttpStatusCode httpStatusCode, out string restStatus, bool getExtendedErrors = false);
    }

    /// <summary>
    /// Exception thrown when a storage detects an Etag inconsistency when attempting to perform a WriteStateAsync operation.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class InconsistentStateException : OrleansException
    {
        /// <summary>
        /// Gets or sets a value indicating whether this exception occurred on the current activation.
        /// </summary>
        [Id(0)]
        internal bool IsSourceActivation { get; set; } = true;

        /// <summary>Gets the Etag value currently held in persistent storage.</summary>
        [Id(1)]
        public string StoredEtag { get; private set; }

        /// <summary>Gets the Etag value currently help in memory, and attempting to be updated.</summary>
        [Id(2)]
        public string CurrentEtag { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="InconsistentStateException"/> class.
        /// </summary>
        public InconsistentStateException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InconsistentStateException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        public InconsistentStateException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InconsistentStateException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The inner exception.</param>
        public InconsistentStateException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InconsistentStateException"/> class.
        /// </summary>
        /// <param name="info">The serialization info.</param>
        /// <param name="context">The context.</param>
        protected InconsistentStateException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.StoredEtag = info.GetString(nameof(StoredEtag));
            this.CurrentEtag = info.GetString(nameof(CurrentEtag));
            this.IsSourceActivation = info.GetBoolean(nameof(this.IsSourceActivation));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InconsistentStateException"/> class.
        /// </summary>
        /// <param name="errorMsg">The error message.</param>
        /// <param name="storedEtag">The stored ETag.</param>
        /// <param name="currentEtag">The current ETag.</param>
        /// <param name="storageException">The inner exception.</param>
        public InconsistentStateException(
          string errorMsg,
          string storedEtag,
          string currentEtag,
          Exception storageException) : base(errorMsg, storageException)
        {
            StoredEtag = storedEtag;
            CurrentEtag = currentEtag;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InconsistentStateException"/> class.
        /// </summary>
        /// <param name="errorMsg">The error message.</param>
        /// <param name="storedEtag">The stored ETag.</param>
        /// <param name="currentEtag">The current ETag.</param>
        public InconsistentStateException(
          string errorMsg,
          string storedEtag,
          string currentEtag)
            : this(errorMsg, storedEtag, currentEtag, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InconsistentStateException"/> class.
        /// </summary>
        /// <param name="storedEtag">The stored ETag.</param>
        /// <param name="currentEtag">The current ETag.</param>
        /// <param name="storageException">The storage exception.</param>
        public InconsistentStateException(string storedEtag, string currentEtag, Exception storageException)
            : this(storageException.Message, storedEtag, currentEtag, storageException)
        {
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return String.Format("InconsistentStateException: {0} Expected Etag={1} Received Etag={2} {3}",
                Message, StoredEtag, CurrentEtag, InnerException);
        }

        /// <inheritdoc/>
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
