using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// This is the base class for all typed grain references.
    /// </summary>
    [Serializable]
    public class GrainReference : IAddressable, IEquatable<GrainReference>, ISerializable
    {
        private static readonly Action<Message, TaskCompletionSource<object>> ResponseCallbackDelegate = ResponseCallback;

        private readonly string genericArguments;
        private readonly GuidId observerId;
        
        [NonSerialized]
        private static readonly Logger logger = LogManager.GetLogger("GrainReference", LoggerType.Runtime);

        [NonSerialized]
        private static ConcurrentDictionary<int, string> debugContexts = new ConcurrentDictionary<int, string>();

        [NonSerialized] private const bool USE_DEBUG_CONTEXT = true;

        [NonSerialized] private const bool USE_DEBUG_CONTEXT_PARAMS = false;

        [NonSerialized]
        private readonly bool isUnordered = false;

        internal bool IsSystemTarget { get { return GrainId.IsSystemTarget; } }

        internal bool IsObserverReference { get { return GrainId.IsClient; } }

        internal GuidId ObserverId { get { return observerId; } }
        
        private bool HasGenericArgument { get { return !String.IsNullOrEmpty(genericArguments); } }

        internal GrainId GrainId { get; private set; }

        /// <summary>
        /// Called from generated code.
        /// </summary>
        protected internal readonly SiloAddress SystemTargetSilo;

        /// <summary>
        /// Whether the runtime environment for system targets has been initialized yet.
        /// Called from generated code.
        /// </summary>
        protected internal bool IsInitializedSystemTarget { get { return SystemTargetSilo != null; } }

        internal bool IsUnordered { get { return isUnordered; } }

        #region Constructors

        /// <summary>Constructs a reference to the grain with the specified Id.</summary>
        /// <param name="grainId">The Id of the grain to refer to.</param>
        /// <param name="genericArgument">Type arguments in case of a generic grain.</param>
        /// <param name="systemTargetSilo">Target silo in case of a system target reference.</param>
        /// <param name="observerId">Observer ID in case of an observer reference.</param>
        private GrainReference(GrainId grainId, string genericArgument, SiloAddress systemTargetSilo, GuidId observerId)
        {
            GrainId = grainId;
            genericArguments = genericArgument;
            SystemTargetSilo = systemTargetSilo;
            this.observerId = observerId;
            if (String.IsNullOrEmpty(genericArgument))
            {
                genericArguments = null; // always keep it null instead of empty.
            }

            // SystemTarget checks
            if (grainId.IsSystemTarget && systemTargetSilo==null)
            {
                throw new ArgumentNullException("systemTargetSilo", String.Format("Trying to create a GrainReference for SystemTarget grain id {0}, but passing null systemTargetSilo.", grainId));
            }
            if (grainId.IsSystemTarget && genericArguments != null)
            {
                throw new ArgumentException(String.Format("Trying to create a GrainReference for SystemTarget grain id {0}, and also passing non-null genericArguments {1}.", grainId, genericArguments), "genericArgument");
            }
            if (grainId.IsSystemTarget && observerId != null)
            {
                throw new ArgumentException(String.Format("Trying to create a GrainReference for SystemTarget grain id {0}, and also passing non-null observerId {1}.", grainId, observerId), "genericArgument");
            }
            if (!grainId.IsSystemTarget && systemTargetSilo != null)
            {
                throw new ArgumentException(String.Format("Trying to create a GrainReference for non-SystemTarget grain id {0}, but passing a non-null systemTargetSilo {1}.", grainId, systemTargetSilo), "systemTargetSilo");
            }

            // ObserverId checks
            if (grainId.IsClient && observerId == null)
            {
                throw new ArgumentNullException("observerId", String.Format("Trying to create a GrainReference for Observer with Client grain id {0}, but passing null observerId.", grainId));
            }
            if (grainId.IsClient && genericArguments != null)
            {
                throw new ArgumentException(String.Format("Trying to create a GrainReference for Client grain id {0}, and also passing non-null genericArguments {1}.", grainId, genericArguments), "genericArgument");
            }
            if (grainId.IsClient && systemTargetSilo != null)
            {
                throw new ArgumentException(String.Format("Trying to create a GrainReference for Client grain id {0}, and also passing non-null systemTargetSilo {1}.", grainId, systemTargetSilo), "genericArgument");
            }
            if (!grainId.IsClient && observerId != null)
            {
                throw new ArgumentException(String.Format("Trying to create a GrainReference with non null Observer {0}, but non Client grain id {1}.", observerId, grainId), "observerId");
            }

            isUnordered = GetUnordered();
        }

        /// <summary>
        /// Constructs a copy of a grain reference.
        /// </summary>
        /// <param name="other">The reference to copy.</param>
        protected GrainReference(GrainReference other)
            : this(other.GrainId, other.genericArguments, other.SystemTargetSilo, other.ObserverId) { }

        #endregion

        #region Instance creator factory functions

        /// <summary>Constructs a reference to the grain with the specified ID.</summary>
        /// <param name="grainId">The ID of the grain to refer to.</param>
        /// <param name="genericArguments">Type arguments in case of a generic grain.</param>
        /// <param name="systemTargetSilo">Target silo in case of a system target reference.</param>
        internal static GrainReference FromGrainId(GrainId grainId, string genericArguments = null, SiloAddress systemTargetSilo = null)
        {
            return new GrainReference(grainId, genericArguments, systemTargetSilo, null);
        }

        internal static GrainReference NewObserverGrainReference(GrainId grainId, GuidId observerId)
        {
            return new GrainReference(grainId, null, null, observerId);
        }

        #endregion

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
        
        public bool Equals(GrainReference other)
        {
            if (other == null)
                return false;

            if (genericArguments != other.genericArguments)
                return false;
            if (!GrainId.Equals(other.GrainId))
            {
                return false;
            }
            if (IsSystemTarget)
            {
                return Equals(SystemTargetSilo, other.SystemTargetSilo);
            }
            if (IsObserverReference)
            {
                return observerId.Equals(other.observerId);
            }
            return true;
        }

        /// <summary> Calculates a hash code for a grain reference. </summary>
        public override int GetHashCode()
        {
            int hash = GrainId.GetHashCode();
            if (IsSystemTarget)
            {
                hash = hash ^ SystemTargetSilo.GetHashCode();
            }
            if (IsObserverReference)
            {
                hash = hash ^ observerId.GetHashCode();
            }
            return hash;
        }

        /// <summary>Get a uniform hash code for this grain reference.</summary>
        public uint GetUniformHashCode()
        {
            // GrainId already includes the hashed type code for generic arguments.
            return GrainId.GetUniformHashCode();
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
            if (((object)reference1) == null)
                return ((object)reference2) == null;

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
            if (((object)reference1) == null)
                return ((object)reference2) != null;

            return !reference1.Equals(reference2);
        }

        #region Protected members

        /// <summary>
        /// Implemented by generated subclasses to return a constant
        /// Implemented in generated code.
        /// </summary>
        protected virtual int InterfaceId
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
        protected virtual string GetMethodName(int interfaceId, int methodId)
        {
            throw new InvalidOperationException("Should be overridden by subclass");
        }

        /// <summary>
        /// Called from generated code.
        /// </summary>
        protected void InvokeOneWayMethod(int methodId, object[] arguments, InvokeMethodOptions options = InvokeMethodOptions.None, SiloAddress silo = null)
        {
            Task<object> resultTask = InvokeMethodAsync<object>(methodId, arguments, options | InvokeMethodOptions.OneWay);
            if (!resultTask.IsCompleted && resultTask.Result != null)
            {
                throw new OrleansException("Unexpected return value: one way InvokeMethod is expected to return null.");
            }
        }

        /// <summary>
        /// Called from generated code.
        /// </summary>
        protected Task<T> InvokeMethodAsync<T>(int methodId, object[] arguments, InvokeMethodOptions options = InvokeMethodOptions.None, SiloAddress silo = null)
        {
            object[] argsDeepCopy = null;
            if (arguments != null)
            {
                CheckForGrainArguments(arguments);
                SetGrainCancellationTokensTarget(arguments, this);
                argsDeepCopy = (object[])SerializationManager.DeepCopy(arguments);
            }
            
            var request = new InvokeMethodRequest(this.InterfaceId, methodId, argsDeepCopy);

            if (IsUnordered)
                options |= InvokeMethodOptions.Unordered;

            Task<object> resultTask = InvokeMethod_Impl(request, null, options);

            if (resultTask == null)
            {
                if (typeof(T) == typeof(object))
                {
                    // optimize for most common case when using one way calls.
                    return PublicOrleansTaskExtensions.CompletedTask as Task<T>;
                }

                return Task.FromResult(default(T));
            }

            resultTask = OrleansTaskExtentions.ConvertTaskViaTcs(resultTask);
            return resultTask.Unbox<T>();
        }

        #endregion

        #region Private members

        private Task<object> InvokeMethod_Impl(InvokeMethodRequest request, string debugContext, InvokeMethodOptions options)
        {
            if (debugContext == null && USE_DEBUG_CONTEXT)
            {
                if (USE_DEBUG_CONTEXT_PARAMS)
                {
#pragma warning disable 162
                    // This is normally unreachable code, but kept for debugging purposes
                    debugContext = GetDebugContext(this.InterfaceName, GetMethodName(this.InterfaceId, request.MethodId), request.Arguments);
#pragma warning restore 162
                }
                else
                {
                    var hash = InterfaceId ^ request.MethodId;
                    if (!debugContexts.TryGetValue(hash, out debugContext))
                    {
                        debugContext = GetDebugContext(this.InterfaceName, GetMethodName(this.InterfaceId, request.MethodId), request.Arguments);
                        debugContexts[hash] = debugContext;
                    }
                }
            }

            // Call any registered client pre-call interceptor function.
            CallClientInvokeCallback(request);

            bool isOneWayCall = ((options & InvokeMethodOptions.OneWay) != 0);

            var resolver = isOneWayCall ? null : new TaskCompletionSource<object>();
            RuntimeClient.Current.SendRequest(this, request, resolver, ResponseCallbackDelegate, debugContext, options, genericArguments);
            return isOneWayCall ? null : resolver.Task;
        }

        private void CallClientInvokeCallback(InvokeMethodRequest request)
        {
            // Make callback to any registered client callback function, allowing opportunity for an application to set any additional RequestContext info, etc.
            // Should we set some kind of callback-in-progress flag to detect and prevent any inappropriate callback loops on this GrainReference?
            try
            {
                Action<InvokeMethodRequest, IGrain> callback = GrainClient.ClientInvokeCallback; // Take copy to avoid potential race conditions
                if (callback == null) return;

                // Call ClientInvokeCallback only for grain calls, not for system targets.
                if (this is IGrain)
                {
                    callback(request, (IGrain) this);
                }
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.ProxyClient_ClientInvokeCallback_Error,
                    "Error while invoking ClientInvokeCallback function " + GrainClient.ClientInvokeCallback,
                    exc);
                throw;
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private static void ResponseCallback(Message message, TaskCompletionSource<object> context)
        {
            Response response;
            if (message.Result != Message.ResponseTypes.Rejection)
            {
                try
                {
                    response = (Response)message.BodyObject;
                }
                catch (Exception exc)
                {
                    //  catch the Deserialize exception and break the promise with it.
                    response = Response.ExceptionResponse(exc);
                }
            }
            else
            {
                Exception rejection;
                switch (message.RejectionType)
                {
                    case Message.RejectionTypes.GatewayTooBusy:
                        rejection = new GatewayTooBusyException();
                        break;
                    case Message.RejectionTypes.DuplicateRequest:
                        return; // Ignore duplicates
                    
                    default:
                        rejection = message.BodyObject as OrleansException;
                        if (rejection == null)
                        {
                            if (string.IsNullOrEmpty(message.RejectionInfo))
                            {
                                message.RejectionInfo = "Unable to send request - no rejection info available";
                            } 
                            rejection = new OrleansException(message.RejectionInfo);
                        }
                        break;
                }
                response = Response.ExceptionResponse(rejection);
            }

            if (!response.ExceptionFlag)
            {
                context.TrySetResult(response.Data);
            }
            else
            {
                context.TrySetException(response.Exception);
            }
        }

        private bool GetUnordered()
        {
            if (RuntimeClient.Current == null) return false;

            return RuntimeClient.Current.GrainTypeResolver != null && RuntimeClient.Current.GrainTypeResolver.IsUnordered(GrainId.GetTypeCode());
        }

        #endregion
        
        private static String GetDebugContext(string interfaceName, string methodName, object[] arguments)
        {
            // String concatenation is approx 35% faster than string.Format here
            //debugContext = String.Format("{0}:{1}()", this.InterfaceName, GetMethodName(this.InterfaceId, methodId));
            var debugContext = new StringBuilder();
            debugContext.Append(interfaceName);
            debugContext.Append(":");
            debugContext.Append(methodName);
            if (USE_DEBUG_CONTEXT_PARAMS && arguments != null && arguments.Length > 0)
            {
                debugContext.Append("(");
                debugContext.Append(Utils.EnumerableToString(arguments));
                debugContext.Append(")");
            }
            else
            {
                debugContext.Append("()");
            }
            return debugContext.ToString();
        }

        private static void CheckForGrainArguments(object[] arguments)
        {
            foreach (var argument in arguments)
                if (argument is Grain)
                    throw new ArgumentException(String.Format("Cannot pass a grain object {0} as an argument to a method. Pass this.AsReference<GrainInterface>() instead.", argument.GetType().FullName));
        }

        /// <summary>
        /// Sets target grain to the found instances of type GrainCancellationToken
        /// </summary>
        /// <param name="arguments"> Grain method arguments list</param>
        /// <param name="target"> Target grain reference</param>
        private static void SetGrainCancellationTokensTarget(object[] arguments, GrainReference target)
        {
            if (arguments == null) return;
            foreach (var argument in arguments)
            {
                (argument as GrainCancellationToken)?.AddGrainReference(target);
            }
        }

        /// <summary> Serializer function for grain reference.</summary>
        /// <seealso cref="SerializationManager"/>
        [SerializerMethod]
        protected internal static void SerializeGrainReference(object obj, ISerializationContext context, Type expected)
        {
            var writer = context.StreamWriter;
            var input = (GrainReference)obj;
            writer.Write(input.GrainId);
            if (input.IsSystemTarget)
            {
                writer.Write((byte)1);
                writer.Write(input.SystemTargetSilo);
            }
            else
            {
                writer.Write((byte)0);
            }

            if (input.IsObserverReference)
            {
                input.observerId.SerializeToStream(writer);
            }

            // store as null, serialize as empty.
            var genericArg = String.Empty;
            if (input.HasGenericArgument)
                genericArg = input.genericArguments;
            writer.Write(genericArg);
        }

        /// <summary> Deserializer function for grain reference.</summary>
        /// <seealso cref="SerializationManager"/>
        [DeserializerMethod]
        protected internal static object DeserializeGrainReference(Type t, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            GrainId id = reader.ReadGrainId();
            SiloAddress silo = null;
            GuidId observerId = null;
            byte siloAddressPresent = reader.ReadByte();
            if (siloAddressPresent != 0)
            {
                silo = reader.ReadSiloAddress();
            }
            bool expectObserverId = id.IsClient;
            if (expectObserverId)
            {
                observerId = GuidId.DeserializeFromStream(reader);
            }
            // store as null, serialize as empty.
            var genericArg = reader.ReadString();
            if (String.IsNullOrEmpty(genericArg))
                genericArg = null;

            if (expectObserverId)
            {
                return NewObserverGrainReference(id, observerId);
            }
            return FromGrainId(id, genericArg, silo);
        }

        /// <summary> Copier function for grain reference. </summary>
        /// <seealso cref="SerializationManager"/>
        [CopierMethod]
        protected internal static object CopyGrainReference(object original, ICopyContext context)
        {
            return (GrainReference)original;
        }

        private const string GRAIN_REFERENCE_STR = "GrainReference";
        private const string SYSTEM_TARGET_STR = "SystemTarget";
        private const string OBSERVER_ID_STR = "ObserverId";
        private const string GENERIC_ARGUMENTS_STR = "GenericArguments";

        /// <summary>Returns a string representation of this reference.</summary>
        public override string ToString()
        {
            if (IsSystemTarget)
            {
                return String.Format("{0}:{1}/{2}", SYSTEM_TARGET_STR, GrainId, SystemTargetSilo);
            }
            if (IsObserverReference)
            {
                return String.Format("{0}:{1}/{2}", OBSERVER_ID_STR, GrainId, observerId);
            }
            return String.Format("{0}:{1}{2}", GRAIN_REFERENCE_STR, GrainId,
                   !HasGenericArgument ? String.Empty : String.Format("<{0}>", genericArguments)); 
        }

        internal string ToDetailedString()
        {
            if (IsSystemTarget)
            {
                return String.Format("{0}:{1}/{2}", SYSTEM_TARGET_STR, GrainId.ToDetailedString(), SystemTargetSilo);
            }
            if (IsObserverReference)
            {
                return String.Format("{0}:{1}/{2}", OBSERVER_ID_STR, GrainId.ToDetailedString(), observerId.ToDetailedString());
            }
            return String.Format("{0}:{1}{2}", GRAIN_REFERENCE_STR, GrainId.ToDetailedString(),
                   !HasGenericArgument ? String.Empty : String.Format("<{0}>", genericArguments)); 
        }


        /// <summary> Get the key value for this grain, as a string. </summary>
        public string ToKeyString()
        {
            if (IsObserverReference)
            {
                return String.Format("{0}={1} {2}={3}", GRAIN_REFERENCE_STR, GrainId.ToParsableString(), OBSERVER_ID_STR, observerId.ToParsableString());
            }
            if (IsSystemTarget)
            {
                return String.Format("{0}={1} {2}={3}", GRAIN_REFERENCE_STR, GrainId.ToParsableString(), SYSTEM_TARGET_STR, SystemTargetSilo.ToParsableString());
            }
            if (HasGenericArgument)
            {
                return String.Format("{0}={1} {2}={3}", GRAIN_REFERENCE_STR, GrainId.ToParsableString(), GENERIC_ARGUMENTS_STR, genericArguments);
            }
            return String.Format("{0}={1}", GRAIN_REFERENCE_STR, GrainId.ToParsableString());
        }

        public static GrainReference FromKeyString(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException("key", "GrainReference.FromKeyString cannot parse null key");
            
            string trimmed = key.Trim();
            string grainIdStr;
            int grainIdIndex = (GRAIN_REFERENCE_STR + "=").Length;

            int genericIndex = trimmed.IndexOf(GENERIC_ARGUMENTS_STR + "=", StringComparison.Ordinal);
            int observerIndex = trimmed.IndexOf(OBSERVER_ID_STR + "=", StringComparison.Ordinal);
            int systemTargetIndex = trimmed.IndexOf(SYSTEM_TARGET_STR + "=", StringComparison.Ordinal);

            if (genericIndex >= 0)
            {
                grainIdStr = trimmed.Substring(grainIdIndex, genericIndex - grainIdIndex).Trim();
                string genericStr = trimmed.Substring(genericIndex + (GENERIC_ARGUMENTS_STR + "=").Length);
                if (String.IsNullOrEmpty(genericStr))
                {
                    genericStr = null;
                }
                return FromGrainId(GrainId.FromParsableString(grainIdStr), genericStr);
            }
            else if (observerIndex >= 0)
            {
                grainIdStr = trimmed.Substring(grainIdIndex, observerIndex - grainIdIndex).Trim();
                string observerIdStr = trimmed.Substring(observerIndex + (OBSERVER_ID_STR + "=").Length);
                GuidId observerId = GuidId.FromParsableString(observerIdStr);
                return NewObserverGrainReference(GrainId.FromParsableString(grainIdStr), observerId);
            }
            else if (systemTargetIndex >= 0)
            {
                grainIdStr = trimmed.Substring(grainIdIndex, systemTargetIndex - grainIdIndex).Trim();
                string systemTargetStr = trimmed.Substring(systemTargetIndex + (SYSTEM_TARGET_STR + "=").Length);
                SiloAddress siloAddress = SiloAddress.FromParsableString(systemTargetStr);
                return FromGrainId(GrainId.FromParsableString(grainIdStr), null, siloAddress);
            }
            else
            {
                grainIdStr = trimmed.Substring(grainIdIndex);
                return FromGrainId(GrainId.FromParsableString(grainIdStr));
            }
            //return FromGrainId(GrainId.FromParsableString(grainIdStr), generic);
        }


        #region ISerializable Members

        public virtual void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            // Use the AddValue method to specify serialized values.
            info.AddValue("GrainId", GrainId.ToParsableString(), typeof(string));
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
            var grainIdStr = info.GetString("GrainId");
            GrainId = GrainId.FromParsableString(grainIdStr);
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
        }

        #endregion
    }
}
