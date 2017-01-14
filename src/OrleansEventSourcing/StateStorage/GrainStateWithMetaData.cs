using Orleans.EventSourcing.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.EventSourcing.StateStorage
{
    /// <summary>
    /// A class that extends grain state with versioning metadata, so that a grain with log-view consistency
    /// can use a standard storage provider via <see cref="LogViewAdaptor{TView,TEntry}"/>
    /// </summary>
    /// <typeparam name="TView">The type used for log view</typeparam>
    [Serializable]
    public class GrainStateWithMetaDataAndETag<TView> : IGrainState where TView : class, new()
    {
        /// <summary>
        /// Gets and Sets StateAndMetaData
        /// </summary>
        public GrainStateWithMetaData<TView> StateAndMetaData { get; set; }
       
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
                StateAndMetaData = (GrainStateWithMetaData<TView>)value;
            }
        }

        /// <summary>
        /// Initialize a new instance of GrainStateWithMetaDataAndETag class with a initialVew
        /// </summary>
        public GrainStateWithMetaDataAndETag(TView initialview)
        {
            StateAndMetaData = new GrainStateWithMetaData<TView>(initialview);
        }

        /// <summary>
        /// Initializes a new instance of GrainStateWithMetaDataAndETag class
        /// </summary>
        public GrainStateWithMetaDataAndETag()
        {
            StateAndMetaData = new GrainStateWithMetaData<TView>();
        }

        /// <summary>
        /// Convert current GrainStateWithMetaDataAndETag object information to a string
        /// </summary>
        public override string ToString()
        {
            return string.Format("v{0} Flags={1} ETag={2} Data={3}", StateAndMetaData.GlobalVersion, StateAndMetaData.WriteVector, ETag, StateAndMetaData.State);
        }
    }


    /// <summary>
    /// A class that extends grain state with versioning metadata, so that a log-consistent grain
    /// can use a standard storage provider via <see cref="LogViewAdaptor{TView,TEntry}"/>
    /// </summary>
    /// <typeparam name="TView"></typeparam>
    [Serializable]
    public class GrainStateWithMetaData<TView> where TView : class, new()
    {
        /// <summary>
        /// The stored view of the log
        /// </summary>
        public TView State { get; set; }

        /// <summary>
        /// The length of the log
        /// </summary>
        public int GlobalVersion { get; set; }


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
        /// Initializes a new instance of the <see cref="GrainStateWithMetaData{TView}"/> class.
        /// </summary>
        public GrainStateWithMetaData()
        {
            State = new TView();
            GlobalVersion = 0;
            WriteVector = "";
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainStateWithMetaData{TView}"/> class.
        /// </summary>
        /// <param name="initialstate">The initial state of the view</param>
        public GrainStateWithMetaData(TView initialstate)
        {
            this.State = initialstate;
            GlobalVersion = 0;
            WriteVector = "";
        }


        /// <summary>
        /// Gets one of the bits in <see cref="WriteVector"/>
        /// </summary>
        /// <param name="Replica">The replica for which we want to look up the bit</param>
        /// <returns></returns>
        public bool GetBit(string Replica)
        {
            return StringEncodedWriteVector.GetBit(WriteVector, Replica);
        }

        /// <summary>
        /// toggle one of the bits in <see cref="WriteVector"/> and return the new value.
        /// </summary>
        /// <param name="Replica">The replica for which we want to flip the bit</param>
        /// <returns>the state of the bit after flipping it</returns>
        public bool FlipBit(string Replica)
        {
            var str = WriteVector;
            var rval = StringEncodedWriteVector.FlipBit(ref str, Replica);
            WriteVector = str;
            return rval;
        }
    }
}
