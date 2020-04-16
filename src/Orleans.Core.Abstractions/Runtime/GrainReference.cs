using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Core;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// This is the base class for all typed grain references.
    /// </summary>
    [Serializable]
    public class GrainReference : IAddressable, IEquatable<GrainReference>, ISerializable
    {
        private readonly string genericArguments;
        private readonly GuidId observerId;

        /// <summary>
        /// Invoke method options specific to this grain reference instance
        /// </summary>
        [NonSerialized]
        private readonly InvokeMethodOptions invokeMethodOptions;

        internal bool IsSystemTarget { get { return GrainId.IsSystemTarget(); } }

        public bool IsGrainService => this.IsSystemTarget;  // TODO make this distinct

        internal bool IsObserverReference { get { return ObserverGrainId.TryParse(GrainId, out _); } }

        internal GuidId ObserverId { get { return observerId; } }
        
        internal bool HasGenericArgument { get { return !String.IsNullOrEmpty(genericArguments); } }

        internal IGrainReferenceRuntime Runtime
        {
            get
            {
                if (this.runtime == null) throw new GrainReferenceNotBoundException(this);
                return this.runtime;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance is bound to a runtime and hence valid for making requests.
        /// </summary>
        internal bool IsBound => this.runtime != null;

        public GrainId GrainId { get; private set; }

        /// <summary>
        /// Called from generated code.
        /// </summary>
        protected internal readonly SiloAddress SystemTargetSilo;

        public SiloAddress GrainServiceSiloAddress => this.SystemTargetSilo;    // TODO make this distinct

        [NonSerialized]
        private IGrainReferenceRuntime runtime;

        /// <summary>
        /// Whether the runtime environment for system targets has been initialized yet.
        /// Called from generated code.
        /// </summary>
        protected internal bool IsInitializedSystemTarget { get { return SystemTargetSilo != null; } }

        internal string GenericArguments => this.genericArguments;

        /// <summary>Constructs a reference to the grain with the specified Id.</summary>
        /// <param name="grainId">The Id of the grain to refer to.</param>
        /// <param name="genericArgument">Type arguments in case of a generic grain.</param>
        /// <param name="runtime">The runtime which this grain reference is bound to.</param>
        private GrainReference(GrainId grainId, string genericArgument, IGrainReferenceRuntime runtime)
        {
            GrainId = grainId;
            this.genericArguments = genericArgument;
            this.runtime = runtime;
            if (string.IsNullOrEmpty(genericArgument))
            {
                genericArguments = null; // always keep it null instead of empty.
            }

            // SystemTarget checks
            var isSystemTarget = grainId.IsSystemTarget();
            if (SystemTargetGrainId.TryParse(grainId, out var systemTargetId))
            {
                this.SystemTargetSilo = systemTargetId.GetSiloAddress();
                if (SystemTargetSilo == null)
                {
                    throw new ArgumentNullException("systemTargetSilo", String.Format("Trying to create a GrainReference for SystemTarget grain id {0}, but passing null systemTargetSilo.", grainId));
                }

                if (genericArguments != null)
                {
                    throw new ArgumentException(String.Format("Trying to create a GrainReference for SystemTarget grain id {0}, and also passing non-null genericArguments {1}.", grainId, genericArguments), "genericArgument");
                }
            }

            // ObserverId checks
            var isClient = grainId.IsClient();
            if (isClient)
            {
                // Note: we can probably just remove this check - it serves little purpose.
                if (!ObserverGrainId.TryParse(grainId, out _))
                {
                    throw new ArgumentNullException("observerId", String.Format("Trying to create a GrainReference for Observer with Client grain id {0}, but passing null observerId.", grainId));
                }

                if (genericArguments != null)
                {
                    throw new ArgumentException(String.Format("Trying to create a GrainReference for Client grain id {0}, and also passing non-null genericArguments {1}.", grainId, genericArguments), "genericArgument");
                }
            }
        }

        /// <summary>
        /// Constructs a copy of a grain reference.
        /// </summary>
        /// <param name="other">The reference to copy.</param>
        protected GrainReference(GrainReference other)
            : this(other.GrainId, other.genericArguments, other.runtime)
        {
            this.invokeMethodOptions = other.invokeMethodOptions;
        }

        protected internal GrainReference(GrainReference other, InvokeMethodOptions invokeMethodOptions)
            : this(other)
        {
            this.invokeMethodOptions = invokeMethodOptions;
        }

        /// <summary>Constructs a reference to the grain with the specified ID.</summary>
        /// <param name="grainId">The ID of the grain to refer to.</param>
        /// <param name="runtime">The runtime client</param>
        /// <param name="genericArguments">Type arguments in case of a generic grain.</param>
        internal static GrainReference FromGrainId(GrainId grainId, IGrainReferenceRuntime runtime, string genericArguments = null)
        {
            return new GrainReference(grainId, genericArguments, runtime);
        }

        internal static GrainReference NewObserverGrainReference(ObserverGrainId observerId, IGrainReferenceRuntime runtime)
        {
            return new GrainReference(observerId.GrainId, null, runtime);
        }

        /// <summary>
        /// Binds this instance to a runtime.
        /// </summary>
        /// <param name="runtime">The runtime.</param>
        internal void Bind(IGrainReferenceRuntime runtime)
        {
            this.runtime = runtime;
        }

        /// <summary>
        /// Tests this reference for equality to another object.
        /// Two grain references are equal if they both refer to the same grain.
        /// </summary>
        /// <param name="obj">The object to test for equality against this reference.</param>
        /// <returns><c>true</c> if the object is equal to this reference.</returns>
        public override bool Equals(object obj)
        {
            return Equals(obj as GrainReference);
        }

        public bool Equals(GrainReference other) => other is GrainReference && this.GrainId.Equals(other.GrainId);

        /// <summary> Calculates a hash code for a grain reference. </summary>
        public override int GetHashCode() => this.GrainId.GetHashCode();

        /// <summary>Get a uniform hash code for this grain reference.</summary>
        public uint GetUniformHashCode()
        {
            // GrainId already includes the hashed type code for generic arguments.
            return (uint)GrainId.GetHashCode();
        }

        /// <summary>
        /// Compares two references for equality.
        /// Two grain references are equal if they both refer to the same grain.
        /// </summary>
        /// <param name="reference1">First grain reference to compare.</param>
        /// <param name="reference2">Second grain reference to compare.</param>
        /// <returns><c>true</c> if both grain references refer to the same grain (by grain identifier).</returns>
        public static bool operator ==(GrainReference reference1, GrainReference reference2)
        {
            if (reference1 is null) return reference2 is null;

            return reference1.Equals(reference2);
        }

        /// <summary>
        /// Compares two references for inequality.
        /// Two grain references are equal if they both refer to the same grain.
        /// </summary>
        /// <param name="reference1">First grain reference to compare.</param>
        /// <param name="reference2">Second grain reference to compare.</param>
        /// <returns><c>false</c> if both grain references are resolved to the same grain (by grain identifier).</returns>
        public static bool operator !=(GrainReference reference1, GrainReference reference2)
        {
            if (reference1 is null) return !(reference2 is null);

            return !reference1.Equals(reference2);
        }

        /// <summary>
        /// Implemented by generated subclasses to return a constant
        /// Implemented in generated code.
        /// </summary>
        public virtual int InterfaceId
        {
            get
            {
                throw new InvalidOperationException("Should be overridden by subclass");
            }
        }

        /// <summary>
        /// Implemented in generated code.
        /// </summary>
        public virtual ushort InterfaceVersion
        {
            get
            {
                throw new InvalidOperationException("Should be overridden by subclass");
            }
        }

        /// <summary>
        /// Implemented in generated code.
        /// </summary>
        public virtual bool IsCompatible(int interfaceId)
        {
            throw new InvalidOperationException("Should be overridden by subclass");
        }

        /// <summary>
        /// Return the name of the interface for this GrainReference. 
        /// Implemented in Orleans generated code.
        /// </summary>
        public virtual string InterfaceName
        {
            get
            {
                throw new InvalidOperationException("Should be overridden by subclass");
            }
        }

        /// <summary>
        /// Return the method name associated with the specified interfaceId and methodId values.
        /// </summary>
        /// <param name="interfaceId">Interface Id</param>
        /// <param name="methodId">Method Id</param>
        /// <returns>Method name string.</returns>
        public virtual string GetMethodName(int interfaceId, int methodId)
        {
            throw new InvalidOperationException("Should be overridden by subclass");
        }

        /// <summary>
        /// Called from generated code.
        /// </summary>
        protected void InvokeOneWayMethod(int methodId, object[] arguments, InvokeMethodOptions options = InvokeMethodOptions.None, SiloAddress silo = null)
        {
            this.Runtime.InvokeOneWayMethod(this, methodId, arguments, options | invokeMethodOptions, silo);
        }

        /// <summary>
        /// Called from generated code.
        /// </summary>
        protected Task<T> InvokeMethodAsync<T>(int methodId, object[] arguments, InvokeMethodOptions options = InvokeMethodOptions.None, SiloAddress silo = null)
        {
            return this.Runtime.InvokeMethodAsync<T>(this, methodId, arguments, options | invokeMethodOptions, silo);
        }

        private const string GRAIN_REFERENCE_STR = "GrainReference";
        private const string SYSTEM_TARGET_STR = "SystemTarget";
        private const string SYSTEM_TARGET_STR_WITH_EQUAL_SIGN = SYSTEM_TARGET_STR + "=";
        private const string OBSERVER_ID_STR = "ObserverId";
        private const string OBSERVER_ID_STR_WITH_EQUAL_SIGN = OBSERVER_ID_STR + "=";
        private const string GENERIC_ARGUMENTS_STR = "GenericArguments";
        private const string GENERIC_ARGUMENTS_STR_WITH_EQUAL_SIGN = GENERIC_ARGUMENTS_STR + "=";

        /// <summary>Returns a string representation of this reference.</summary>
        public override string ToString()
        {
            return $"{GRAIN_REFERENCE_STR}:{GrainId}{(!HasGenericArgument ? String.Empty : String.Format("<{0}>", genericArguments))}"; 
        }

        /// <summary> Get the key value for this grain, as a string. </summary>
        public string ToKeyString()
        {
            if (IsObserverReference)
            {
                return String.Format("{0}={1} {2}={3}", GRAIN_REFERENCE_STR, ((LegacyGrainId)GrainId).ToParsableString(), OBSERVER_ID_STR, observerId.ToParsableString());
            }
            if (IsSystemTarget)
            {
                return String.Format("{0}={1} {2}={3}", GRAIN_REFERENCE_STR, ((LegacyGrainId)GrainId).ToParsableString(), SYSTEM_TARGET_STR, SystemTargetSilo.ToParsableString());
            }
            if (HasGenericArgument)
            {
                return String.Format("{0}={1} {2}={3}", GRAIN_REFERENCE_STR, ((LegacyGrainId)GrainId).ToParsableString(), GENERIC_ARGUMENTS_STR, genericArguments);
            }
            return String.Format("{0}={1}", GRAIN_REFERENCE_STR, ((LegacyGrainId)GrainId).ToParsableString());
        }
        
        internal static GrainReference FromKeyString(string key, IGrainReferenceRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key), "GrainReference.FromKeyString cannot parse null key");

            ReadOnlySpan<char> trimmed = key.AsSpan().Trim();
            ReadOnlySpan<char> grainIdStr;
            int grainIdIndex = (GRAIN_REFERENCE_STR + "=").Length;

            int genericIndex = trimmed.IndexOf(GENERIC_ARGUMENTS_STR_WITH_EQUAL_SIGN.AsSpan(), StringComparison.Ordinal);
            int observerIndex = trimmed.IndexOf(OBSERVER_ID_STR_WITH_EQUAL_SIGN.AsSpan(), StringComparison.Ordinal);
            int systemTargetIndex = trimmed.IndexOf(SYSTEM_TARGET_STR_WITH_EQUAL_SIGN.AsSpan(), StringComparison.Ordinal);

            if (genericIndex >= 0)
            {
                grainIdStr = trimmed.Slice(grainIdIndex, genericIndex - grainIdIndex).Trim();
                ReadOnlySpan<char> genericStr = trimmed.Slice(genericIndex + GENERIC_ARGUMENTS_STR_WITH_EQUAL_SIGN.Length);
                return FromGrainId(LegacyGrainId.FromParsableString(grainIdStr), runtime, genericStr.ToString());
            }
            else if (observerIndex >= 0)
            {
                grainIdStr = trimmed.Slice(grainIdIndex, observerIndex - grainIdIndex).Trim();
                return FromGrainId(LegacyGrainId.FromParsableString(grainIdStr), runtime);
            }
            else if (systemTargetIndex >= 0)
            {
                grainIdStr = trimmed.Slice(grainIdIndex, systemTargetIndex - grainIdIndex).Trim();
                ReadOnlySpan<char> systemTargetStr = trimmed.Slice(systemTargetIndex + SYSTEM_TARGET_STR_WITH_EQUAL_SIGN.Length);

                // TODO: Incorporate SiloAddress into GrainId or entirely remove FromKeyString - perhaps shift to a legacy/compat library
                _ = SiloAddress.FromParsableString(systemTargetStr.ToString());
                return FromGrainId(LegacyGrainId.FromParsableString(grainIdStr), runtime, null);
            }
            else
            {
                grainIdStr = trimmed.Slice(grainIdIndex);
                return FromGrainId(LegacyGrainId.FromParsableString(grainIdStr), runtime);
            }
        }

        internal static GrainReference FromKeyInfo(GrainReferenceKeyInfo keyInfo, IGrainReferenceRuntime runtime)
        {
            if (keyInfo.HasGenericArgument)
            {
                return FromGrainId(LegacyGrainId.FromKeyInfo(keyInfo.Key), runtime, keyInfo.GenericArgument);
            }
            else if (keyInfo.HasTargetSilo)
            {
                // TODO: Incorporate SiloAddress into GrainId - perhaps shift to a legacy/compat library
                _ = SiloAddress.New(keyInfo.TargetSilo.endpoint, keyInfo.TargetSilo.generation);
                return FromGrainId(LegacyGrainId.FromKeyInfo(keyInfo.Key), runtime, null);
            }
            else
            {
                return FromGrainId(LegacyGrainId.FromKeyInfo(keyInfo.Key), runtime);
            }
        }


        public virtual void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            // Use the AddValue method to specify serialized values.
            info.AddValue("GrainId", GrainId, typeof(GrainId));
            if (IsSystemTarget)
            {
                info.AddValue("SystemTargetSilo", SystemTargetSilo.ToParsableString(), typeof(string));
            }
            if (IsObserverReference)
            {
                info.AddValue(OBSERVER_ID_STR, observerId.ToParsableString(), typeof(string));
            }
            string genericArg = String.Empty;
            if (HasGenericArgument)
                genericArg = genericArguments;
            info.AddValue("GenericArguments", genericArg, typeof(string));
        }

        // The special constructor is used to deserialize values. 
        protected GrainReference(SerializationInfo info, StreamingContext context)
        {
            // Reset the property value using the GetValue method.
            GrainId = (GrainId)info.GetValue("GrainId", typeof(GrainId));
            if (IsSystemTarget)
            {
                var siloAddressStr = info.GetString("SystemTargetSilo");
                SystemTargetSilo = SiloAddress.FromParsableString(siloAddressStr);
            }
            if (IsObserverReference)
            {
                var observerIdStr = info.GetString(OBSERVER_ID_STR);
                observerId = GuidId.FromParsableString(observerIdStr);
            }
            var genericArg = info.GetString("GenericArguments");
            if (String.IsNullOrEmpty(genericArg))
                genericArg = null;
            genericArguments = genericArg;

            var serializerContext = context.Context as ISerializerContext;
            this.runtime = serializerContext?.ServiceProvider.GetService(typeof(IGrainReferenceRuntime)) as IGrainReferenceRuntime;
        }
    }
}
