using Orleans.EventSourcing.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.EventSourcing.LogStorage
{
    /// <summary>
    /// A class that extends grain state with versioning metadata, so that a grain with log-view consistency
    /// can use a standard storage provider via <see cref="LogViewAdaptor{TView,TEntry}"/>
    /// </summary>
    /// <typeparam name="TEntry">The type used for log entries</typeparam>
    [Serializable]
    public class LogStateWithMetaDataAndETag<TEntry> : IGrainState where TEntry : class
    {
        /// <summary>
        /// Gets and Sets StateAndMetaData
        /// </summary>
        public LogStateWithMetaData<TEntry> StateAndMetaData { get; set; }
       
        /// <summary>
        /// Gets and Sets Etag
        /// </summary>
        public string ETag { get; set; }

        object IGrainState.State
        {
            get
            {
                return StateAndMetaData;
            }
            set
            {
                StateAndMetaData = (LogStateWithMetaData<TEntry>)value;
            }
        }

        /// <summary>
        /// Initializes a new instance of GrainStateWithMetaDataAndETag class
        /// </summary>
        public LogStateWithMetaDataAndETag()
        {
            StateAndMetaData = new LogStateWithMetaData<TEntry>();
        }

        /// <summary>
        /// Convert current GrainStateWithMetaDataAndETag object information to a string
        /// </summary>
        public override string ToString()
        {
            return string.Format("v{0} Flags={1} ETag={2} Data={3}", StateAndMetaData.GlobalVersion, StateAndMetaData.WriteVector, ETag, StateAndMetaData.Log);
        }
    }


    /// <summary>
    /// A class that extends grain state with versioning metadata, so that a log-consistent grain
    /// can use a standard storage provider via <see cref="LogViewAdaptor{TView,TEntry}"/>
    /// </summary>
    /// <typeparam name="TEntry"></typeparam>
    [Serializable]
    public class LogStateWithMetaData<TEntry> where TEntry : class
    {
        /// <summary>
        /// The stored view of the log
        /// </summary>
        public List<TEntry> Log { get; set; }

        /// <summary>
        /// The length of the log
        /// </summary>
        public int GlobalVersion { get { return Log.Count; } }


        /// <summary>
        /// Metadata that is used to avoid duplicate appends.
        /// Logically, this is a (string->bit) map, the keys being replica ids
        /// But this map is represented compactly as a simple string to reduce serialization/deserialization overhead
        /// Bits are read by <see cref="GetBit"/> and flipped by  <see cref="FlipBit"/>.
        /// Bits are toggled when writing, so that the retry logic can avoid appending an entry twice
        /// when retrying a failed append. 
        /// </summary>
        public string WriteVector { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="LogStateWithMetaData{TView}"/> class.
        /// </summary>
        public LogStateWithMetaData()
        {
            Log = new List<TEntry>();
            WriteVector = "";
        }


        /// <summary>
        /// Gets one of the bits in <see cref="WriteVector"/>
        /// </summary>
        /// <param name="Replica">The replica for which we want to look up the bit</param>
        /// <returns></returns>
        public bool GetBit(string Replica) {
            return StringEncodedWriteVector.GetBit(WriteVector, Replica);
        }

        /// <summary>
        /// toggle one of the bits in <see cref="WriteVector"/> and return the new value.
        /// </summary>
        /// <param name="Replica">The replica for which we want to flip the bit</param>
        /// <returns>the state of the bit after flipping it</returns>
        public bool FlipBit(string Replica) {
            var str = WriteVector;
            var rval = StringEncodedWriteVector.FlipBit(ref str, Replica);
            WriteVector = str;
            return rval;
        }


    }
}
