using Orleans.CodeGeneration;
using Orleans.EventSourcing.Common;
using Orleans.Serialization;
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
    public class GrainStateWithMetaDataAndETag<TView> : GrainStateWithMetaData<TView>, IGrainState where TView : class, new()
    {       
        /// <summary>
        /// Gets and Sets Etag
        /// </summary>
        public string ETag { get; set; }

        object IGrainState.State
        {
            get
            {
                return State;
            }
            set
            {
                State = (TView)value;
            }
        }

        /// <summary>
        /// Initialize a new instance of GrainStateWithMetaDataAndETag class from deserialized values
        /// </summary>
        protected GrainStateWithMetaDataAndETag(string etag, TView initialview, int globalVersion, string writeVector) : base(initialview, globalVersion, writeVector) { }

        /// <summary>
        /// Initialize a new instance of GrainStateWithMetaDataAndETag class with a initialVew
        /// </summary>
        public GrainStateWithMetaDataAndETag(TView initialview) : base(initialview) { }

        /// <summary>
        /// Initializes a new instance of GrainStateWithMetaDataAndETag class
        /// </summary>
        public GrainStateWithMetaDataAndETag() : base() { }

        /// <summary>
        /// Convert current GrainStateWithMetaDataAndETag object information to a string
        /// </summary>
        public override string ToString()
        {
            return string.Format("v{0} Flags={1} ETag={2} Data={3}", GlobalVersion, WriteVector, ETag, State);
        }

        [CopierMethod]
        public static object DeepCopier(object original, ICopyContext context)
        {
            GrainStateWithMetaDataAndETag<TView> instance = (GrainStateWithMetaDataAndETag<TView>)original;

            string etag = (string)SerializationManager.DeepCopyInner(instance.ETag, context);
            TView state = (TView)SerializationManager.DeepCopyInner(instance.State, context);
            int globalVersion = (int)SerializationManager.DeepCopyInner(instance.GlobalVersion, context);
            string writeVector = (string)SerializationManager.DeepCopyInner(instance.WriteVector, context);

            return new GrainStateWithMetaDataAndETag<TView>(etag, state, globalVersion, writeVector);
        }

        [SerializerMethod]
        internal static void Serialize(object input, ISerializationContext context, Type expected)
        {
            GrainStateWithMetaDataAndETag<TView> instance = (GrainStateWithMetaDataAndETag<TView>)input;

            SerializationManager.SerializeInner(instance.ETag, context, typeof(string));
            SerializationManager.SerializeInner(instance.State, context, typeof(TView));
            SerializationManager.SerializeInner(instance.GlobalVersion, context, typeof(int));
            SerializationManager.SerializeInner(instance.WriteVector, context, typeof(string));
        }

        [DeserializerMethod]
        internal static object Deserialize(Type expected, IDeserializationContext context)
        {
            string etag = SerializationManager.DeserializeInner<string>(context);
            TView state = SerializationManager.DeserializeInner<TView>(context);
            int globalVersion = SerializationManager.DeserializeInner<int>(context);
            string writeVector = SerializationManager.DeserializeInner<string>(context);

            return new GrainStateWithMetaDataAndETag<TView>(etag, state, globalVersion, writeVector);
        }
    }


    /// <summary>
    /// A class that extends grain state with versioning metadata, so that a log-consistent grain
    /// can use a standard storage provider via <see cref="LogViewAdaptor{TView,TEntry}"/>
    /// </summary>
    /// <typeparam name="TView"></typeparam>
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
        /// Initialize a new instance of GrainStateWithMetaDataAndETag class from deserialized values
        /// </summary>
        protected GrainStateWithMetaData(TView initialview, int globalVersion, string writeVector)
        {
            State = initialview;
            GlobalVersion = globalVersion;
            WriteVector = writeVector;
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

        [CopierMethod]
        public static object DeepCopier(object original, ICopyContext context)
        {
            GrainStateWithMetaData<TView> instance = (GrainStateWithMetaData<TView>)original;
            
            TView state = (TView)SerializationManager.DeepCopyInner(instance.State, context);
            int globalVersion = (int)SerializationManager.DeepCopyInner(instance.GlobalVersion, context);
            string writeVector = (string)SerializationManager.DeepCopyInner(instance.WriteVector, context);

            return new GrainStateWithMetaData<TView>(state, globalVersion, writeVector);
        }

        [SerializerMethod]
        internal static void Serialize(object input, ISerializationContext context, Type expected)
        {
            GrainStateWithMetaData<TView> instance = (GrainStateWithMetaData<TView>)input;
            
            SerializationManager.SerializeInner(instance.State, context, typeof(TView));
            SerializationManager.SerializeInner(instance.GlobalVersion, context, typeof(int));
            SerializationManager.SerializeInner(instance.WriteVector, context, typeof(string));
        }

        [DeserializerMethod]
        internal static object Deserialize(Type expected, IDeserializationContext context)
        {
            TView state = SerializationManager.DeserializeInner<TView>(context);
            int globalVersion = SerializationManager.DeserializeInner<int>(context);
            string writeVector = SerializationManager.DeserializeInner<string>(context);

            return new GrainStateWithMetaData<TView>(state, globalVersion, writeVector);
        }
    }
}
