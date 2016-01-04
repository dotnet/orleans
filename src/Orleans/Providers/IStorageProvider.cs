using System;
using System.Net;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Providers;

namespace Orleans.Storage
{
    /// <summary>
    /// Interface to be implemented for a storage provider able to read and write Orleans grain state data.
    /// </summary>
    public interface IStorageProvider : IProvider
    {
        /// <summary>TraceLogger used by this storage provider instance.</summary>
        /// <returns>Reference to the TraceLogger object used by this provider.</returns>
        /// <seealso cref="Logger"/>
        Logger Log { get; }

        /// <summary>Read data function for this storage provider instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">State data object to be populated for this grain.</param>
        /// <returns>Completion promise for the Read operation on the specified grain.</returns>
        Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);

        /// <summary>Write data function for this storage provider instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">State data object to be written for this grain.</param>
        /// <returns>Completion promise for the Write operation on the specified grain.</returns>
        Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);

        /// <summary>Delete / Clear data function for this storage provider instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">Copy of last-known state data object for this grain.</param>
        /// <returns>Completion promise for the Delete operation on the specified grain.</returns>
        Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);
    }

    /// <summary>
    /// Interface to be optionally implemented by storage providers to return richer exception details.
    /// </summary>
    public interface IRestExceptionDecoder
    {
        /// <summary>
        /// Decode details of the exceprion
        /// </summary>
        /// <param name="e">Excption to decode</param>
        /// <param name="httpStatusCode">HTTP status code for the error</param>
        /// <param name="restStatus">REST status for the error</param>
        /// <param name="getExtendedErrors">Whether or not to extract REST error code</param>
        /// <returns></returns>
        bool DecodeException(Exception e, out HttpStatusCode httpStatusCode, out string restStatus, bool getRESTErrors = false);
    }

    /// <summary>
    /// Exception thrown whenever a grain call is attempted with a bad / missing storage provider configuration settings for that grain.
    /// </summary>
    [Serializable]
    public class BadProviderConfigException : OrleansException
    {
        public BadProviderConfigException()
        {}
        public BadProviderConfigException(string msg)
            : base(msg)
        { }
        public BadProviderConfigException(string msg, Exception exc)
            : base(msg, exc)
        { }
        protected BadProviderConfigException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }

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
        {}
        public InconsistentStateException(string msg)
            : base(msg)
        { }
        public InconsistentStateException(string msg, Exception exc)
            : base(msg, exc)
        { }
        protected InconsistentStateException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {}

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
    }
}
